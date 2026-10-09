using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MarkSmith.Core.Preview
{
    // Run #51, second batch: matrices, pyramids, targets, steps, vertical and bending processes,
    // equations, hierarchy lists and block lists. 47 layouts drew as 14 pictures (all four
    // matrices were the same 2x2 grid; Funnel was an inverted pyramid; Interlocking Rings was a
    // Linear Venn).
    public static partial class HtmlPreviewRenderer
    {
        // ------------------------------------------------------------------ shared shapes

        /// <summary>A plus or an equals sign as shapes (text is stripped from gallery tiles, so an
        /// operator drawn as a glyph vanished there).</summary>
        private static void OpSign(StringBuilder sb, double cx, double cy, char op, double size, string fill)
        {
            double t = size * 0.22;
            if (op == '+')
            {
                sb.Append($"<rect x=\"{F(cx - size / 2)}\" y=\"{F(cy - t / 2)}\" width=\"{F(size)}\" height=\"{F(t)}\" rx=\"{F(t / 3)}\" fill=\"{fill}\"/>");
                sb.Append($"<rect x=\"{F(cx - t / 2)}\" y=\"{F(cy - size / 2)}\" width=\"{F(t)}\" height=\"{F(size)}\" rx=\"{F(t / 3)}\" fill=\"{fill}\"/>");
            }
            else
            {
                sb.Append($"<rect x=\"{F(cx - size / 2)}\" y=\"{F(cy - t * 1.4)}\" width=\"{F(size)}\" height=\"{F(t)}\" rx=\"{F(t / 3)}\" fill=\"{fill}\"/>");
                sb.Append($"<rect x=\"{F(cx - size / 2)}\" y=\"{F(cy + t * 0.4)}\" width=\"{F(size)}\" height=\"{F(t)}\" rx=\"{F(t / 3)}\" fill=\"{fill}\"/>");
            }
        }

        /// <summary>An opening quote mark ("66") as shapes, its blobs' bottom at (x, y).</summary>
        private static void QuoteMark(StringBuilder sb, double x, double y, double size, string fill)
        {
            double r = size * 0.2;
            foreach (double ox in new[] { r, r * 3.4 })
            {
                double cx = x + ox, cy = y - r;
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{fill}\"/>");
                sb.Append($"<polygon points=\"{F(cx - r * 0.95)},{F(cy - r * 0.2)} {F(cx + r * 0.4)},{F(cy - r * 2.5)} {F(cx + r * 1.05)},{F(cy - r * 2.1)} {F(cx + r * 0.3)},{F(cy - r * 0.1)}\" fill=\"{fill}\"/>");
            }
        }

        /// <summary>A box with one rounded corner (0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right).</summary>
        private static string OneCornerD(double x, double y, double w, double h, double r, int corner)
        {
            string tl = corner == 0 ? $"M{F(x)} {F(y + r)}A{F(r)} {F(r)} 0 0 1 {F(x + r)} {F(y)}" : $"M{F(x)} {F(y)}";
            string tr = corner == 1 ? $"H{F(x + w - r)}A{F(r)} {F(r)} 0 0 1 {F(x + w)} {F(y + r)}" : $"H{F(x + w)}";
            string br = corner == 3 ? $"V{F(y + h - r)}A{F(r)} {F(r)} 0 0 1 {F(x + w - r)} {F(y + h)}" : $"V{F(y + h)}";
            string bl = corner == 2 ? $"H{F(x + r)}A{F(r)} {F(r)} 0 0 1 {F(x)} {F(y + h - r)}" : $"H{F(x)}";
            return tl + tr + br + bl + "Z";
        }

        // ------------------------------------------------------------------ matrices

        internal enum MatrixStyle { Titled, QuadArrow, Diamond, Cycle }

        /// <summary>Titled Matrix (corners rounded outward round a title), Grid Matrix (cells over a
        /// four-way arrow), Basic Matrix (cells on a diamond) and Cycle Matrix (a ring of wedges in
        /// the middle, each item's detail in the corner card beside its wedge).</summary>
        private static (double, double) DrawMatrixStyled(StringBuilder sb, List<Item> items, MatrixStyle style)
        {
            Item? title = items.Count == 1 && items[0].Children.Count > 0 ? items[0] : null;
            var cells = title != null ? title.Children : items;
            double side = 520, size = 420, m = style switch { MatrixStyle.QuadArrow => 30, MatrixStyle.Diamond => 58, _ => 0 };
            double gap = style switch { MatrixStyle.Titled => 8, MatrixStyle.Cycle => 14, _ => 22 };
            double x0 = (BaseW - side) / 2, y0 = Pad, cx = BaseW / 2, cy = y0 + size / 2;
            double cw = (side - m * 2 - gap) / 2, ch = (size - m * 2 - gap) / 2;
            if (style == MatrixStyle.QuadArrow)
            {
                // A four-way arrow behind the cells, its heads at the matrix's edges.
                double a = 28, hw = 50, hl = 30;
                var pts = new List<(double, double)>();
                for (int k = 0; k < 4; k++)
                {
                    // One arm, pointing up, rotated k quarter turns about the centre.
                    var arm = new (double x, double y)[] { (-a, -a), (-a, -size / 2 + hl), (-hw, -size / 2 + hl), (0, -size / 2), (hw, -size / 2 + hl), (a, -size / 2 + hl) };
                    double ang = k * Math.PI / 2, c = Math.Cos(ang), s = Math.Sin(ang), scale = k % 2 == 1 ? side / size : 1;
                    foreach (var (px, py) in arm) pts.Add((cx + (px * c - py * scale * s), cy + (px * s + py * scale * c)));
                }
                sb.Append($"<polygon points=\"{string.Join(" ", pts.Select(p => $"{F(p.Item1)},{F(p.Item2)}"))}\" fill=\"#d2d0ce\"/>");
            }
            else if (style == MatrixStyle.Diamond)
                sb.Append($"<polygon points=\"{F(cx)},{F(y0)} {F(x0 + side)},{F(cy)} {F(cx)},{F(y0 + size)} {F(x0)},{F(cy)}\" fill=\"{Tint(Accent(0), 0.75)}\"/>");
            for (int i = 0; i < 4; i++)
            {
                double x = x0 + m + (i % 2) * (cw + gap), y = y0 + m + (i / 2) * (ch + gap);
                if (i >= cells.Count)
                {
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(ch)}\" rx=\"8\" fill=\"#ffffff\" stroke=\"#c8c6c4\" stroke-dasharray=\"6 4\"/>");
                    sb.Append($"<text x=\"{F(x + cw / 2)}\" y=\"{F(y + ch / 2 + 5)}\" text-anchor=\"middle\" font-size=\"13\" fill=\"#a19f9d\">{MatrixDefaultTitles[i]}</text>");
                    continue;
                }
                var it = cells[i];
                string c = Accent(i);
                switch (style)
                {
                    case MatrixStyle.Titled:
                    {
                        // Each quadrant rounds the corner that points away from the title.
                        Group(sb, it);
                        sb.Append($"<path d=\"{OneCornerD(x, y, cw, ch, 40, i)}\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                        double tx = i % 2 == 0 ? x : x + cw * 0.32, ty = i / 2 == 0 ? y : y + ch * 0.3;
                        Text(sb, tx, ty, cw * 0.68, ch * 0.7, it.Text, it.Bullets, "#ffffff", maxFs: 15);
                        EndGroup(sb);
                        break;
                    }
                    case MatrixStyle.Cycle:
                    {
                        Group(sb, it);
                        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(ch)}\" rx=\"14\" fill=\"{Tint(c, 0.84)}\"/>");
                        // The detail sits in the card's outer part, clear of the ring of wedges.
                        double clear = 118;
                        double tx = i % 2 == 0 ? x : x + clear, ty = i / 2 == 0 ? y : y + clear * 0.55;
                        Text(sb, tx, ty, cw - clear, ch - clear * 0.55, "", it.Bullets.Count > 0 ? it.Bullets : new[] { it.Text }, Ink, maxFs: 13, alignLeft: true, inset: 12);
                        EndGroup(sb);
                        break;
                    }
                    default:
                        Box(sb, x, y, cw, ch, c, it, rx: 12);
                        break;
                }
            }
            if (style == MatrixStyle.Cycle)
            {
                double R = 104;
                int n = Math.Min(4, cells.Count);
                for (int i = 0; i < n; i++)
                {
                    // Each wedge is the inner corner of its item's card. Wedge q spans angles
                    // q*90+180 to q*90+270 degrees (y down): q 0 is top-left, 1 top-right, 2
                    // bottom-right, 3 bottom-left; the cards go TL, TR, BL, BR.
                    int q = i switch { 0 => 0, 1 => 1, 2 => 3, _ => 2 };
                    double a1 = Math.PI / 2 * q + Math.PI, a2 = a1 + Math.PI / 2, am = (a1 + a2) / 2;
                    Group(sb, cells[i]);
                    sb.Append($"<path d=\"{WedgeD(cx, cy, 0, R, a1 + 0.03, a2 - 0.03)}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                    double lx = cx + R * 0.55 * Math.Cos(am), ly = cy + R * 0.55 * Math.Sin(am);
                    Text(sb, lx - 40, ly - 26, 80, 52, cells[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 13, inset: 2);
                    EndGroup(sb);
                }
                // Arrows chase each other round the outside of the wedges.
                for (int k = 0; k < 4; k++)
                {
                    double a = -Math.PI / 2 + k * Math.PI / 2;
                    sb.Append($"<path d=\"{ArcD(cx, cy, R + 12, a + 0.25, a + Math.PI / 2 - 0.25)}\" fill=\"none\" stroke=\"#8a8886\" stroke-width=\"3\"/>");
                    double e = a + Math.PI / 2 - 0.25;
                    Tri(sb, cx + (R + 12) * Math.Cos(e), cy + (R + 12) * Math.Sin(e), e + Math.PI / 2, 12, "#8a8886");
                }
            }
            if (title != null)
            {
                if (style == MatrixStyle.Titled)
                {
                    double tw = 210, th = 76;
                    Group(sb, title);
                    sb.Append($"<rect x=\"{F(cx - tw / 2)}\" y=\"{F(cy - th / 2)}\" width=\"{F(tw)}\" height=\"{F(th)}\" rx=\"12\" fill=\"#ffffff\" stroke=\"{Tint(Accent(0), 0.4)}\" stroke-width=\"3\"/>");
                    Text(sb, cx - tw / 2, cy - th / 2, tw, th, title.Text, Array.Empty<string>(), Ink, maxFs: 16);
                    EndGroup(sb);
                }
                else Circle(sb, cx, cy, style == MatrixStyle.Cycle ? 46 : 62, "#ffffff", new Item { Text = title.Text }, withBullets: false, textColor: Ink);
            }
            if (cells.Count > 4)
                sb.Append($"<text x=\"{F(BaseW / 2)}\" y=\"{F(y0 + size + 22)}\" text-anchor=\"middle\" font-size=\"12\" fill=\"#605e5c\">A matrix shows four items — {cells.Count - 4} more not shown.</text>");
            return (BaseW, y0 + size + (cells.Count > 4 ? 34 : 0) + Pad);
        }

        // ------------------------------------------------------------------ pyramids and funnels

        /// <summary>Pyramid List: one pale triangle with each item's card stacked across it.</summary>
        private static (double, double) DrawPyramidList(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double cardW = 340, gap = 10, cx = BaseW / 2;
            var hs = items.Select(i => Math.Max(42, Measure(i.Text, i.Bullets, cardW - 24) + 14)).ToList();
            double top = Pad + 70, H = 70 + hs.Sum() + gap * (n - 1) + 24;
            double baseW = Math.Min(BaseW - Pad * 2, Math.Max(cardW + 160, H * 1.3));
            sb.Append($"<polygon points=\"{F(cx)},{F(Pad)} {F(cx + baseW / 2)},{F(Pad + H)} {F(cx - baseW / 2)},{F(Pad + H)}\" fill=\"{Tint(Accent(0), 0.72)}\"/>");
            double y = top;
            for (int i = 0; i < n; i++)
            {
                Group(sb, items[i]);
                sb.Append($"<rect x=\"{F(cx - cardW / 2)}\" y=\"{F(y)}\" width=\"{F(cardW)}\" height=\"{F(hs[i])}\" rx=\"10\" fill=\"#ffffff\" stroke=\"{Accent(i)}\" stroke-width=\"2\"/>");
                Text(sb, cx - cardW / 2, y, cardW, hs[i], items[i].Text, items[i].Bullets, Ink, maxFs: 14);
                EndGroup(sb);
                y += hs[i] + gap;
            }
            return (BaseW, Pad + H + Pad);
        }

        /// <summary>Segmented Pyramid: a triangle cut into small triangles, one item each, row by row.</summary>
        private static (double, double) DrawSegmentedPyramid(StringBuilder sb, List<Item> nodes)
        {
            int n = nodes.Count, rows = (int)Math.Ceiling(Math.Sqrt(n));
            double rowH = Math.Min(120, 420.0 / rows), triW = rowH * 1.16, cx = BaseW / 2;
            int idx = 0;
            for (int r = 0; r < rows; r++)
            {
                double yt = Pad + r * rowH, yb = yt + rowH, left = cx - (r + 1) * triW / 2;
                for (int k = 0; k < 2 * r + 1; k++)
                {
                    int j = k / 2;
                    bool up = k % 2 == 0;
                    double lx = left + j * triW;
                    string pts = up
                        ? $"{F(lx)},{F(yb)} {F(lx + triW)},{F(yb)} {F(lx + triW / 2)},{F(yt)}"
                        : $"{F(lx + triW / 2)},{F(yt)} {F(lx + triW * 1.5)},{F(yt)} {F(lx + triW)},{F(yb)}";
                    if (idx >= n)
                    {
                        sb.Append($"<polygon points=\"{pts}\" fill=\"#edebe9\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                        continue;
                    }
                    Group(sb, nodes[idx]);
                    sb.Append($"<polygon points=\"{pts}\" fill=\"{Accent(idx)}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                    // The text box sits in the widest part of the triangle: its base half.
                    double tcx = up ? lx + triW / 2 : lx + triW, ty = up ? yt + rowH * 0.45 : yt + rowH * 0.06;
                    Text(sb, tcx - triW * 0.3, ty, triW * 0.6, rowH * 0.48, nodes[idx].Text, Array.Empty<string>(), "#ffffff", maxFs: 13, inset: 1);
                    EndGroup(sb);
                    idx++;
                }
            }
            return (BaseW, Pad * 2 + rows * rowH);
        }

        /// <summary>Funnel: every item but the last drops into the funnel; the last is what comes out.</summary>
        private static (double, double) DrawFunnel(StringBuilder sb, List<Item> items)
        {
            var inputs = items.Take(Math.Max(0, items.Count - 1)).ToList();
            var result = items[^1];
            const int maxIn = 6;
            double cx = BaseW / 2, mouth = 380, rimRy = 22, r = 44, y = Pad;
            int shown = Math.Min(maxIn, inputs.Count), perRow = shown <= 3 ? Math.Max(1, shown) : (shown + 1) / 2;
            int inRows = shown == 0 ? 0 : (shown + perRow - 1) / perRow;
            for (int i = 0; i < shown; i++)
            {
                int row = i / perRow, inRow = Math.Min(perRow, shown - row * perRow);
                double step = Math.Min(r * 2 + 10, (mouth - 40) / Math.Max(1, inRow - 1 + 0.001));
                double px = cx + (i % perRow - (inRow - 1) / 2.0) * Math.Min(step, r * 2 + 10), py = y + r + row * (r * 1.7);
                Circle(sb, px, py, r, Accent(i + 1), inputs[i], withBullets: false, opacity: 0.92);
            }
            double rimY = y + (inRows == 0 ? rimRy : r * 2 + (inRows - 1) * r * 1.7 + 4);
            if (inputs.Count > shown) { Note(sb, cx + mouth / 2 + 6, rimY - 6, inputs.Count - shown, "start"); }
            double neckW = 70, bodyH = 150, neckH = 34, neckY = rimY + bodyH;
            string body = Tint(Accent(0), 0.55);
            sb.Append($"<polygon points=\"{F(cx - mouth / 2)},{F(rimY)} {F(cx + mouth / 2)},{F(rimY)} {F(cx + neckW / 2)},{F(neckY)} {F(cx + neckW / 2)},{F(neckY + neckH)} {F(cx - neckW / 2)},{F(neckY + neckH)} {F(cx - neckW / 2)},{F(neckY)}\" fill=\"{body}\"/>");
            sb.Append($"<path d=\"M{F(cx - mouth / 2)} {F(rimY)}A{F(mouth / 2)} {F(rimRy)} 0 0 1 {F(cx + mouth / 2)} {F(rimY)}A{F(mouth / 2)} {F(rimRy)} 0 0 1 {F(cx - mouth / 2)} {F(rimY)}Z\" fill=\"{Tint(Accent(0), 0.3)}\" fill-opacity=\"0.6\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
            double ay = neckY + neckH + 8;
            BlockArrow(sb, cx - 24, ay, 48, 40, 'D', "#c8c6c4");
            double rw = 300, rh = Math.Max(56, Measure(result.Text, result.Bullets, rw - 24) + 20), ry = ay + 48;
            Box(sb, cx - rw / 2, ry, rw, rh, Accent(0), result, rx: 10);
            return (BaseW, ry + rh + Pad);
        }

        // ------------------------------------------------------------------ targets and rings

        /// <summary>Basic Target: rings round one centre, each labelled out to the right.</summary>
        private static (double, double) DrawBasicTarget(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 7);
            double rMax = 170, rMin = Math.Max(30, rMax / (n + 0.5)), step = n == 1 ? 0 : (rMax - rMin) / (n - 1);
            double cx = Pad + rMax, cy = Pad + rMax, labelX = cx + rMax + 36, labelW = BaseW - Pad - labelX;
            double R(int i) => rMax - i * step;
            for (int i = 0; i < n; i++)
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(R(i))}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
            double rowH = Math.Min(2 * rMax / n, 96);
            double rowsTop = cy - rowH * n / 2;
            for (int i = 0; i < n; i++)
            {
                // A leader from the middle of the ring's own band, out to its row of the list.
                double band = i < n - 1 ? R(i) - step / 2 : R(i) * 0.4;
                double a = -0.7 + 1.4 * (n == 1 ? 0.5 : (double)i / (n - 1));
                double px = cx + band * Math.Cos(a), py = cy + band * Math.Sin(a), ly = rowsTop + (i + 0.5) * rowH;
                Group(sb, items[i]);
                sb.Append($"<path d=\"M{F(px)} {F(py)}L{F(cx + rMax + 14)} {F(ly)}H{F(labelX - 6)}\" fill=\"none\" stroke=\"{Ink}\" stroke-width=\"1\" stroke-opacity=\"0.45\"/>");
                sb.Append($"<circle cx=\"{F(px)}\" cy=\"{F(py)}\" r=\"3.5\" fill=\"#ffffff\" stroke=\"{Ink}\" stroke-width=\"1\"/>");
                Text(sb, labelX, ly - rowH / 2, labelW, rowH, items[i].Text, items[i].Bullets, Ink, maxFs: 14, alignLeft: true, inset: 2);
                EndGroup(sb);
            }
            if (items.Count > n) Note(sb, labelX, cy + rMax + 16, items.Count - n, "start");
            return (BaseW, cy + rMax + Pad + (items.Count > n ? 16 : 0));
        }

        /// <summary>Nested Target: each item a rounded panel inside the one before, its label in the
        /// band along its top.</summary>
        private static (double, double) DrawNestedTarget(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 6);
            const double inset = 26;
            double x = Pad, w = BaseW - Pad * 2, y = Pad;
            var bands = items.Take(n).Select((it, i) => Math.Max(44, Measure(it.Text, it.Bullets, w - inset * 2 * i - 30) + 14)).ToList();
            // Each panel ends a step above the one round it, so every band keeps its full height.
            const double foot = 12;
            double total = bands.Sum() + n * foot;
            for (int i = 0; i < n; i++)
            {
                double bx = x + inset * i, bw = w - inset * 2 * i, by = y + bands.Take(i).Sum(), bh = total - bands.Take(i).Sum() - foot * i;
                Group(sb, items[i]);
                sb.Append($"<rect x=\"{F(bx)}\" y=\"{F(by)}\" width=\"{F(bw)}\" height=\"{F(bh)}\" rx=\"16\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                Text(sb, bx, by, bw, bands[i], items[i].Text, items[i].Bullets, "#ffffff", maxFs: 15, inset: 14);
                EndGroup(sb);
            }
            if (items.Count > n) { Note(sb, BaseW / 2, y + total + 16, items.Count - n); total += 16; }
            return (BaseW, y + total + Pad);
        }

        /// <summary>Target List: a quarter target in the corner, a row per ring listing its item.</summary>
        private static (double, double) DrawTargetList(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double listX = Pad + 200, listW = BaseW - Pad - listX;
            var hs = items.Select(i => Math.Max(46, Measure(i.Text, i.Bullets, listW - 30) + 14)).ToList();
            double H = Math.Max(200, hs.Sum() + 8 * (n - 1));
            double ox = Pad, oy = Pad + H, rMax = Math.Min(H, 190);
            for (int i = 0; i < n; i++)
            {
                double r = rMax * (n - i) / n;
                sb.Append($"<path d=\"M{F(ox)} {F(oy)}V{F(oy - r)}A{F(r)} {F(r)} 0 0 1 {F(ox + r)} {F(oy)}Z\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
            }
            double y = Pad;
            for (int i = 0; i < n; i++)
            {
                Group(sb, items[i]);
                sb.Append($"<rect x=\"{F(listX)}\" y=\"{F(y)}\" width=\"{F(listW)}\" height=\"{F(hs[i])}\" rx=\"6\" fill=\"{Tint(Accent(i), 0.86)}\"/>");
                sb.Append($"<rect x=\"{F(listX)}\" y=\"{F(y)}\" width=\"8\" height=\"{F(hs[i])}\" rx=\"3\" fill=\"{Accent(i)}\"/>");
                Text(sb, listX + 10, y, listW - 10, hs[i], items[i].Text, items[i].Bullets, Ink, maxFs: 14, alignLeft: true);
                EndGroup(sb);
                y += hs[i] + 8;
            }
            return (BaseW, Pad + H + Pad);
        }

        /// <summary>Interlocking Rings: open rings linked in a row, each label inside its ring.</summary>
        private static (double, double) DrawInterlockingRings(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double r = Math.Min(100, (BaseW - Pad * 2) / (2 + (n - 1) * 1.55)), step = r * 1.55;
            double x0 = (BaseW - (2 * r + (n - 1) * step)) / 2 + r, cy = Pad + r;
            for (int i = 0; i < n; i++)
                sb.Append($"<circle cx=\"{F(x0 + i * step)}\" cy=\"{F(cy)}\" r=\"{F(r - 7)}\" fill=\"none\" stroke=\"{Accent(i)}\" stroke-width=\"14\"/>");
            for (int i = 0; i < n; i++)
            {
                Group(sb, items[i]);
                Text(sb, x0 + i * step - r * 0.55, cy - r * 0.5, r * 1.1, r, items[i].Text, Array.Empty<string>(), Ink, maxFs: 14, inset: 2, thumbBars: false);
                EndGroup(sb);
            }
            return (BaseW, cy + r + Pad);
        }

        // ------------------------------------------------------------------ steps

        /// <summary>Increasing Circle Process: a row per step, its circle filled further each time.</summary>
        private static (double, double) DrawIncreasingCircles(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double r = 30, tx = Pad + r * 2 + 24, tw = BaseW - Pad - tx, y = Pad;
            for (int i = 0; i < n; i++)
            {
                double h = Math.Max(r * 2 + 10, Measure(items[i].Text, items[i].Bullets, tw - 8) + 14), cy = y + h / 2, cx = Pad + r;
                string c = Accent(i);
                Group(sb, items[i]);
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{Tint(c, 0.82)}\"/>");
                double share = (i + 1.0) / n;
                if (share >= 0.999) sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{c}\"/>");
                else sb.Append($"<path d=\"{WedgeD(cx, cy, 0, r, -Math.PI / 2, -Math.PI / 2 + 2 * Math.PI * share)}\" fill=\"{c}\"/>");
                sb.Append($"<rect x=\"{F(tx - 12)}\" y=\"{F(y + 4)}\" width=\"3\" height=\"{F(h - 8)}\" fill=\"{Tint(c, 0.5)}\"/>");
                Text(sb, tx, y, tw, h, items[i].Text, items[i].Bullets, Ink, maxFs: 14, alignLeft: true, inset: 2);
                EndGroup(sb);
                y += h + 10;
            }
            return (BaseW, y - 10 + Pad);
        }

        /// <summary>Ascending Picture Accent Process: round pictures climbing to the right, each
        /// with its text beside it.</summary>
        private static (double, double) DrawAscendingPictures(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double r = 40, colW = Math.Max(150, (BaseW - Pad * 2) / n), w = Math.Max(BaseW, Pad * 2 + n * colW);
            double rise = Math.Min(70, 300.0 / Math.Max(1, n - 1));
            double labelH = Math.Max(48, items.Max(i => Measure(i.Text, i.Bullets, colW - 16, 13)) + 12);
            double baseY = Pad + (n - 1) * rise + r;
            for (int i = 0; i < n; i++)
            {
                double cx = Pad + i * colW + r + 6, cy = baseY - i * rise;
                Group(sb, items[i]);
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r + 5)}\" fill=\"{Accent(i)}\"/>");
                Picture(sb, cx - r, cy - r, r * 2, r * 2, circle: true);
                Text(sb, Pad + i * colW, cy + r + 10, colW - 8, labelH, items[i].Text, items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 4);
                EndGroup(sb);
            }
            return (w, baseY + r + 10 + labelH + Pad);
        }

        /// <summary>Upward Arrow and Descending Process: one broad sweeping arrow, a dot on it per
        /// step, each step's text beside its dot.</summary>
        private static (double, double) DrawSwoosh(StringBuilder sb, List<Item> items, bool up)
        {
            int n = items.Count;
            double w = BaseW, H = 300, x0 = Pad, x1 = BaseW - Pad - 40;
            double colW = (x1 - x0) / n;
            double labelH = Math.Max(40, items.Max(i => Measure(i.Text, i.Bullets, colW - 12, 13)) + 10);
            // A fall's labels sit above its dots, so its top leaves them room.
            double yHigh = up ? Pad + 20 : Pad + labelH + 24, yLow = yHigh + H - 20;
            // A quadratic sweep: flat at the start, steep at the end (rising) or the mirror (falling).
            (double x, double y) P(double t)
            {
                double ay = up ? yLow : yHigh, by = ay, cy2 = up ? yHigh : yLow, bx = x0 + (x1 - x0) * (up ? 0.62 : 0.38);
                double u = 1 - t;
                return (u * u * x0 + 2 * u * t * bx + t * t * x1, u * u * ay + 2 * u * t * by + t * t * cy2);
            }
            var top = new List<string>(); var bot = new List<string>();
            const int steps = 40;
            for (int k = 0; k <= steps; k++)
            {
                double t = (double)k / steps, th = 4 + 22 * t;
                var (px, py) = P(t);
                var (qx, qy) = P(Math.Min(1, t + 0.01));
                double dx = qx - px, dy = qy - py, len = Math.Max(0.001, Math.Sqrt(dx * dx + dy * dy)), nx = -dy / len, ny = dx / len;
                top.Add($"{F(px + nx * th / 2)},{F(py + ny * th / 2)}");
                bot.Insert(0, $"{F(px - nx * th / 2)},{F(py - ny * th / 2)}");
            }
            string band = Tint(Accent(0), 0.5);
            sb.Append($"<polygon points=\"{string.Join(" ", top.Concat(bot))}\" fill=\"{band}\"/>");
            var (ex, ey) = P(1); var (dx0, dy0) = P(0.98);
            double ang = Math.Atan2(ey - dy0, ex - dx0);
            Tri(sb, ex + 34 * Math.Cos(ang), ey + 34 * Math.Sin(ang), ang, 48, band);
            double maxY = yLow + 20;
            for (int i = 0; i < n; i++)
            {
                double t = (i + 0.5) / n;
                var (px, py) = P(t);
                Group(sb, items[i]);
                sb.Append($"<circle cx=\"{F(px)}\" cy=\"{F(py)}\" r=\"14\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                // Text goes where the sweep has left room: below and right of a climbing dot,
                // above and right of a falling one.
                double need = Measure(items[i].Text, items[i].Bullets, colW - 4, 13) + 8;
                double ly = up ? py + 26 : py - 22 - need;
                double lx = Math.Min(px - 12, w - Pad - colW);
                TopText(sb, lx, ly, colW, items[i].Text, items[i].Bullets, Ink, 13, alignLeft: true, inset: 2);
                maxY = Math.Max(maxY, ly + need);
                EndGroup(sb);
            }
            return (w, maxY + Pad);
        }

        /// <summary>Block Descending List: columns stepping down in height, title on top, detail under it.</summary>
        private static (double, double) DrawDescendingBlocks(StringBuilder sb, List<Item> items) => InColumns(items, 6, 10, (part, first, y, cw) =>
        {
            int n = part.Count;
            double need = part.Max(i => Measure(i.Text, i.Bullets, cw - 20)) + 30;
            double Hmax = Math.Max(need + (n - 1) * 34, 180);
            for (int c = 0; c < n; c++)
            {
                double h = Hmax - c * 34, x = ColX(c, cw, 10);
                Group(sb, part[c]);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + Hmax - h)}\" width=\"{F(cw)}\" height=\"{F(h)}\" rx=\"4\" fill=\"{Accent(first + c)}\"/>");
                Text(sb, x, y + Hmax - h + 6, cw, Math.Min(h - 12, need), part[c].Text, part[c].Bullets, "#ffffff", maxFs: 14);
                EndGroup(sb);
            }
            return Hmax;
        });

        // ------------------------------------------------------------------ vertical processes

        /// <summary>Staggered Process: boxes stepping across to the right, a bent arrow down to each next one.</summary>
        private static (double, double) DrawStaggeredProcess(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double bw = 330, stepX = n > 1 ? (BaseW - Pad * 2 - bw) / (n - 1) : 0, y = Pad;
            for (int i = 0; i < n; i++)
            {
                double x = Pad + i * stepX, h = Math.Max(54, Measure(items[i].Text, items[i].Bullets, bw - 20) + 22);
                Box(sb, x, y, bw, h, Accent(i), items[i], rx: 12);
                y += h;
                if (i < n - 1)
                {
                    // Down from under this box, then across to the next one's left third.
                    double sx = x + bw * 0.25, ex = Pad + (i + 1) * stepX + 36;
                    sb.Append($"<path d=\"M{F(sx)} {F(y + 2)}V{F(y + 20)}H{F(ex - 8)}\" fill=\"none\" stroke=\"#c8c6c4\" stroke-width=\"8\"/>");
                    Tri(sb, ex + 10, y + 20, 0, 22, "#c8c6c4");
                    y += 40;
                }
            }
            return (BaseW, y + Pad);
        }

        /// <summary>Vertical Arrow List: each label in a box, its detail in a broad arrow pointing on from it.</summary>
        private static (double, double) DrawVerticalArrowList(StringBuilder sb, List<Item> items)
        {
            double lw = 190, ax = Pad + lw + 10, aw = BaseW - Pad - ax, y = Pad;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double h = Math.Max(64, Math.Max(Measure(it.Text, Array.Empty<string>(), lw - 20), Measure("", it.Bullets, aw - 110, 13) / 0.74) + 18);
                Group(sb, it);
                BlockArrow(sb, ax, y, aw, h, 'R', Tint(c, 0.8), shaft: 0.74);
                if (it.Children.Count > 0) Text(sb, ax + 8, y + h * 0.13, aw - Math.Min(h * 0.75, aw * 0.4) - 16, h * 0.74, "", it.Bullets, Ink, maxFs: 13, alignLeft: true);
                sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y)}\" width=\"{F(lw)}\" height=\"{F(h)}\" rx=\"10\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                Text(sb, Pad, y, lw, h, it.Text, Array.Empty<string>(), "#ffffff", maxFs: 15);
                EndGroup(sb);
                y += h + 10;
            }
            return (BaseW, y - 10 + Pad);
        }

        /// <summary>Chevron rows: each item a chevron, its sub-items following it as paler chevrons.</summary>
        private static (double, double) DrawChevronRows(StringBuilder sb, List<Item> items)
        {
            const double ch = 60, point = 20, gap = 12;
            int maxKids = Math.Max(1, items.Max(i => i.Children.Count));
            double headW = 190, kidW = Math.Min(170, (BaseW - Pad * 2 - headW - 8) / maxKids);
            double y = Pad;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                Group(sb, it);
                sb.Append($"<polygon points=\"{ChevronPts(Pad, y, headW, ch, point, true)}\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                Text(sb, Pad + 4, y, headW - point - 4, ch, it.Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                for (int k = 0; k < it.Children.Count; k++)
                {
                    double x = Pad + headW - 6 + k * (kidW - 6);
                    sb.Append($"<polygon points=\"{ChevronPts(x, y + 6, kidW, ch - 12, point - 4, false)}\" fill=\"{Tint(c, 0.75)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                    Text(sb, x + point, y + 6, kidW - point * 2 + 4, ch - 12, it.Children[k].Text, Array.Empty<string>(), Ink, maxFs: 12, inset: 2);
                }
                EndGroup(sb);
                y += ch + gap;
            }
            return (BaseW, y - gap + Pad);
        }

        /// <summary>Process columns: each item heads a column; its sub-items step down it, arrow by arrow.</summary>
        private static (double, double) DrawProcessColumns(StringBuilder sb, List<Item> items) => InColumns(items, 5, 18, (part, first, y, cw) =>
        {
            double headH = Math.Max(48, part.Max(i => Measure(i.Text, Array.Empty<string>(), cw - 20)) + 14);
            double kidH = Math.Max(40, part.SelectMany(i => i.Children).Select(k => Measure(k.Text, k.Bullets, cw - 40, 13) + 12).DefaultIfEmpty(0).Max());
            double tallest = headH;
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 18), ky = y + headH;
                string col = Accent(first + c);
                Group(sb, part[c]);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(headH)}\" rx=\"10\" fill=\"{col}\"/>");
                Text(sb, x, y, cw, headH, part[c].Text, Array.Empty<string>(), "#ffffff", maxFs: 15);
                foreach (var k in part[c].Children)
                {
                    DownArrow(sb, x + cw / 2, ky + 4, 26, 20);
                    ky += 28;
                    sb.Append($"<rect x=\"{F(x + 12)}\" y=\"{F(ky)}\" width=\"{F(cw - 24)}\" height=\"{F(kidH)}\" rx=\"8\" fill=\"{Tint(col, 0.82)}\"/>");
                    Text(sb, x + 12, ky, cw - 24, kidH, k.Text, k.Bullets, Ink, maxFs: 13, inset: 6);
                    ky += kidH;
                }
                EndGroup(sb);
                tallest = Math.Max(tallest, ky - y);
            }
            return tallest;
        });

        // ------------------------------------------------------------------ bending processes

        internal enum SnakeStyle { Columns, Repeating, Circles }

        /// <summary>Vertical Bending Process (down each column, then over to the next), Repeating
        /// Bending Process (rows that turn back on themselves) and Circular Bending Process (circles).</summary>
        private static (double, double) DrawSnake(StringBuilder sb, List<Item> items, SnakeStyle style)
        {
            int n = items.Count;
            bool circles = style == SnakeStyle.Circles, columns = style == SnakeStyle.Columns;
            int per = columns ? (n <= 3 ? n : n <= 6 ? 3 : 4) : (n <= 4 ? Math.Max(1, n) : n <= 9 ? 3 : 4);
            int lines = (n + per - 1) / per;
            double gapX = 50, gapY = 46;
            double bw, bh;
            if (columns)
            {
                bw = Math.Min(240, (BaseW - Pad * 2 - (lines - 1) * gapX) / lines);
                bh = Math.Min(160, Math.Max(64, items.Max(i => Measure(i.Text, i.Bullets, bw - 20)) + 22));
            }
            else if (circles)
            {
                bw = bh = Math.Min(150, Math.Max(2 * RadiusForWords(items) + 10, (BaseW - Pad * 2 - (per - 1) * gapX) / per * 0.8));
            }
            else
            {
                bw = (BaseW - Pad * 2 - (per - 1) * gapX) / per;
                bh = Math.Min(200, Math.Max(76, items.Max(i => Measure(i.Text, i.Bullets, bw - 20)) + 24));
            }
            double spanX = columns ? lines * bw + (lines - 1) * gapX : per * bw + (per - 1) * gapX, ox = (BaseW - spanX) / 2;
            (double x, double y) Pos(int i)
            {
                int line = i / per, k = i % per;
                if (columns) return (ox + line * (bw + gapX), Pad + k * (bh + gapY));
                if (style == SnakeStyle.Repeating && line % 2 == 1) k = per - 1 - k;
                return (ox + k * (bw + gapX), Pad + line * (bh + gapY));
            }
            for (int i = 0; i < n - 1; i++)
            {
                var (ax, ay) = Pos(i);
                var (bx, by) = Pos(i + 1);
                double acx = ax + bw / 2, acy = ay + bh / 2, bcx = bx + bw / 2, bcy = by + bh / 2;
                if (Math.Abs(ay - by) < 1)
                {
                    // Along a row, either way.
                    double dir = Math.Sign(bx - ax), sx = acx + dir * (bw / 2 + 6), ex = bcx - dir * (bw / 2 + 8);
                    sb.Append($"<line x1=\"{F(sx)}\" y1=\"{F(acy)}\" x2=\"{F(ex - dir * 10)}\" y2=\"{F(bcy)}\" stroke=\"{Connector}\" stroke-width=\"3\"/>");
                    Tri(sb, ex, bcy, dir > 0 ? 0 : Math.PI, 14, Connector);
                }
                else if (Math.Abs(ax - bx) < 1)
                {
                    // Down a column, or the turn at the end of a repeating row.
                    if (style == SnakeStyle.Repeating)
                    {
                        double side = ax > BaseW / 2 ? ax + bw + 18 : ax - 18;
                        double edge = ax > BaseW / 2 ? ax + bw + 4 : ax - 4;
                        sb.Append($"<path d=\"M{F(edge)} {F(acy)}H{F(side)}V{F(bcy)}H{F(edge + (ax > BaseW / 2 ? 10 : -10))}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"3\"/>");
                        Tri(sb, edge, bcy, ax > BaseW / 2 ? Math.PI : 0, 14, Connector);
                    }
                    else
                    {
                        sb.Append($"<line x1=\"{F(acx)}\" y1=\"{F(ay + bh + 6)}\" x2=\"{F(bcx)}\" y2=\"{F(by - 18)}\" stroke=\"{Connector}\" stroke-width=\"3\"/>");
                        Tri(sb, bcx, by - 6, Math.PI / 2, 14, Connector);
                    }
                }
                else if (columns)
                {
                    // From the foot of one column up to the head of the next.
                    double midX = ax + bw + gapX / 2;
                    sb.Append($"<path d=\"M{F(ax + bw + 4)} {F(acy)}H{F(midX)}V{F(bcy)}H{F(bx - 16)}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"3\"/>");
                    Tri(sb, bx - 4, bcy, 0, 14, Connector);
                }
                else
                {
                    double midY = ay + bh + gapY / 2;
                    sb.Append($"<path d=\"M{F(acx)} {F(ay + bh + 4)}V{F(midY)}H{F(bcx)}V{F(by - 16)}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"3\"/>");
                    Tri(sb, bcx, by - 4, Math.PI / 2, 14, Connector);
                }
            }
            for (int i = 0; i < n; i++)
            {
                var (x, y) = Pos(i);
                if (circles) Circle(sb, x + bw / 2, y + bh / 2, bw / 2, Accent(i), items[i]);
                else Box(sb, x, y, bw, bh, Accent(i), items[i], rx: 14);
            }
            double bottom = Enumerable.Range(0, n).Max(i => Pos(i).y) + bh;
            return (BaseW, bottom + Pad);
        }

        // ------------------------------------------------------------------ equations

        /// <summary>Vertical Equation: the parts stacked with plus signs between, an arrow to the result.</summary>
        private static (double, double) DrawVerticalEquation(StringBuilder sb, List<Item> items)
        {
            if (items.Count == 1) { Circle(sb, BaseW / 2, 130, 100, Accent(0), items[0]); return (BaseW, 260); }
            var parts = items.Take(items.Count - 1).ToList();
            double r = Math.Min(70, Math.Max(46, RadiusForWords(parts) + 6)), opH = 40, cx = Pad + 160;
            double y = Pad;
            for (int i = 0; i < parts.Count; i++)
            {
                Circle(sb, cx, y + r, r, Accent(0), parts[i]);
                y += 2 * r;
                if (i < parts.Count - 1) { OpSign(sb, cx, y + opH / 2, '+', 26, "#8a8886"); y += opH; }
            }
            double midY = (Pad + y) / 2, R = Math.Min(110, Math.Max(r * 1.3, (y - Pad) / 3));
            BlockArrow(sb, cx + r + 40, midY - 30, 140, 60, 'R', "#c8c6c4");
            Circle(sb, cx + r + 200 + R, midY, R, Accent(2), items[^1]);
            return (BaseW, Math.Max(y, midY + R) + Pad);
        }

        /// <summary>Converging Text: the parts down the left, each line converging on the result box.</summary>
        private static (double, double) DrawConvergingText(StringBuilder sb, List<Item> items)
        {
            if (items.Count == 1) { Box(sb, BaseW / 2 - 160, Pad, 320, 120, Accent(0), items[0], rx: 12); return (BaseW, 120 + Pad * 2); }
            var parts = items.Take(items.Count - 1).ToList();
            double pw = 260, ph = 52, gap = 14;
            var hs = parts.Select(p => Math.Max(ph, Measure(p.Text, p.Bullets, pw - 30) + 16)).ToList();
            double H = hs.Sum() + gap * (parts.Count - 1), rw = 220, rx = BaseW - Pad - rw;
            double rh = Math.Max(110, Measure(items[^1].Text, items[^1].Bullets, rw - 24) + 30), ry = Pad + (H - rh) / 2;
            double y = Pad;
            for (int i = 0; i < parts.Count; i++)
            {
                double cy = y + hs[i] / 2;
                sb.Append($"<line x1=\"{F(Pad + pw)}\" y1=\"{F(cy)}\" x2=\"{F(rx - 16)}\" y2=\"{F(Math.Clamp(cy, ry + 18, ry + rh - 18))}\" stroke=\"{Tint(Accent(i + 1), 0.3)}\" stroke-width=\"3\"/>");
                double ang = Math.Atan2(Math.Clamp(cy, ry + 18, ry + rh - 18) - cy, rx - 16 - Pad - pw);
                Tri(sb, rx - 2, Math.Clamp(cy, ry + 18, ry + rh - 18), ang, 16, Tint(Accent(i + 1), 0.3));
                Group(sb, parts[i]);
                sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y)}\" width=\"{F(pw)}\" height=\"{F(hs[i])}\" rx=\"{F(hs[i] / 2)}\" fill=\"{Accent(i + 1)}\"/>");
                Text(sb, Pad, y, pw, hs[i], parts[i].Text, parts[i].Bullets, "#ffffff", maxFs: 14, inset: 16);
                EndGroup(sb);
                y += hs[i] + gap;
            }
            Box(sb, rx, Math.Max(Pad, ry), rw, rh, Accent(0), items[^1], rx: 6);
            return (BaseW, Math.Max(y - gap, ry + rh) + Pad);
        }

        /// <summary>Random to Result: a scatter of dots for where it starts, the steps as chevrons,
        /// and a solid circle for the result.</summary>
        private static (double, double) DrawRandomToResult(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double cy = Pad + 80, labelY = cy + 84;
            double startW = 150, resultR = 64;
            var mids = n > 2 ? items.Skip(1).Take(n - 2).ToList() : new List<Item>();
            double chevW = mids.Count == 0 ? 0 : Math.Min(150, (BaseW - Pad * 2 - startW - resultR * 2 - 40) / mids.Count);
            double labelH = Math.Max(40, items.Max(i => Measure(i.Text, i.Bullets, Math.Max(chevW, startW) - 12, 13)) + 10);
            // The scatter: a fixed, irregular cloud of dots.
            var dots = new (double dx, double dy, double r)[] { (-50, -40, 12), (-8, -56, 9), (34, -34, 14), (-58, 6, 10), (-14, -6, 16), (32, 14, 10), (-36, 46, 13), (12, 48, 9), (54, -2, 8) };
            double sx = Pad + startW / 2;
            Group(sb, items[0]);
            for (int k = 0; k < dots.Length; k++)
                sb.Append($"<circle cx=\"{F(sx + dots[k].dx)}\" cy=\"{F(cy + dots[k].dy)}\" r=\"{F(dots[k].r)}\" fill=\"{Tint(Accent(k % 4), 0.25)}\"/>");
            TopText(sb, Pad, labelY, startW, items[0].Text, items[0].Bullets, Ink, 13);
            EndGroup(sb);
            double x = Pad + startW + 16;
            for (int i = 0; i < mids.Count; i++)
            {
                Group(sb, mids[i]);
                sb.Append($"<polygon points=\"{ChevronPts(x, cy - 26, chevW, 52, 18, false)}\" fill=\"{Accent(i + 1)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                TopText(sb, x, labelY, chevW, mids[i].Text, mids[i].Bullets, Ink, 13);
                EndGroup(sb);
                x += chevW - 4;
            }
            if (n > 1)
            {
                double rcx = x + 18 + resultR;
                Group(sb, items[^1]);
                sb.Append($"<circle cx=\"{F(rcx)}\" cy=\"{F(cy)}\" r=\"{F(resultR)}\" fill=\"{Accent(0)}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                Text(sb, rcx - resultR * 0.75, cy - resultR * 0.65, resultR * 1.5, resultR * 1.3, items[^1].Text, items[^1].Bullets, "#ffffff", maxFs: 14, inset: 2);
                EndGroup(sb);
            }
            return (BaseW, labelY + labelH + Pad);
        }

        // ------------------------------------------------------------------ hierarchy lists

        /// <summary>Lined List: each item's title on the left under a heavy rule, its sub-items to
        /// the right, each under a light rule of its own.</summary>
        private static (double, double) DrawLinedList(StringBuilder sb, List<Item> items)
        {
            double titleW = 210, kx = Pad + titleW + 16, kw = BaseW - Pad - kx, y = Pad;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                var kidH = it.Children.Select(k => Math.Max(34, Measure(k.Text, k.Bullets, kw - 10, 13) + 10)).ToList();
                double h = Math.Max(Math.Max(44, Measure(it.Text, Array.Empty<string>(), titleW - 10) + 14), kidH.Sum());
                Group(sb, it);
                sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y)}\" width=\"{F(BaseW - Pad * 2)}\" height=\"5\" fill=\"{c}\"/>");
                TopText(sb, Pad, y + 10, titleW, it.Text, Array.Empty<string>(), Ink, 16, alignLeft: true, inset: 2);
                double ky = y + 5;
                for (int k = 0; k < it.Children.Count; k++)
                {
                    if (k > 0) sb.Append($"<rect x=\"{F(kx)}\" y=\"{F(ky)}\" width=\"{F(kw)}\" height=\"1.5\" fill=\"{Tint(c, 0.5)}\"/>");
                    Text(sb, kx, ky + 2, kw, kidH[k], it.Children[k].Text, it.Children[k].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 4);
                    ky += kidH[k];
                }
                EndGroup(sb);
                y += h + 20;
            }
            return (BaseW, y - 20 + Pad);
        }

        /// <summary>Grouped List: each item a tall tinted panel, its title on top and its sub-items
        /// as cards inside it.</summary>
        private static (double, double) DrawGroupedList(StringBuilder sb, List<Item> items) => InColumns(items, 5, 16, (part, first, y, cw) =>
        {
            double headH = Math.Max(44, part.Max(i => Measure(i.Text, Array.Empty<string>(), cw - 20)) + 12);
            var kidH = part.Select(i => i.Children.Select(k => Math.Max(38, Measure(k.Text, k.Bullets, cw - 40, 13) + 12)).ToList()).ToList();
            double panelH = headH + Math.Max(20, kidH.Max(l => l.Sum() + l.Count * 8)) + 10;
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 16);
                string col = Accent(first + c);
                Group(sb, part[c]);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(panelH)}\" rx=\"14\" fill=\"{Tint(col, 0.78)}\"/>");
                Text(sb, x, y, cw, headH, part[c].Text, Array.Empty<string>(), Ink, maxFs: 15);
                double ky = y + headH;
                for (int k = 0; k < part[c].Children.Count; k++)
                {
                    sb.Append($"<rect x=\"{F(x + 10)}\" y=\"{F(ky)}\" width=\"{F(cw - 20)}\" height=\"{F(kidH[c][k])}\" rx=\"6\" fill=\"{col}\"/>");
                    Text(sb, x + 10, ky, cw - 20, kidH[c][k], part[c].Children[k].Text, part[c].Children[k].Bullets, "#ffffff", maxFs: 13, inset: 6);
                    ky += kidH[c][k] + 8;
                }
                EndGroup(sb);
            }
            return panelH;
        });

        /// <summary>Circle-headed columns joined by two-way arrows, each column's sub-items stacked under its circle.</summary>
        private static (double, double) DrawLinkedCircleColumns(StringBuilder sb, List<Item> items) => InColumns(items, 4, 40, (part, first, y, cw) =>
        {
            double r = Math.Min(cw / 2 - 4, Math.Max(48, Math.Min(66, RadiusForWords(part) + 8)));
            var kidH = part.Select(i => i.Children.Select(k => Math.Max(36, Measure(k.Text, k.Bullets, cw - 20, 13) + 10)).ToList()).ToList();
            double tallest = r * 2;
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 40), cx = x + cw / 2;
                string col = Accent(first + c);
                if (c < part.Count - 1)
                {
                    double ax0 = cx + r + 4, ax1 = ColX(c + 1, cw, 40) + cw / 2 - r - 4;
                    if (ax1 - ax0 > 20) BlockArrow(sb, ax0, y + r - 12, (ax1 - ax0) / 2, 24, 'L', "#c8c6c4");
                    if (ax1 - ax0 > 20) BlockArrow(sb, (ax0 + ax1) / 2, y + r - 12, (ax1 - ax0) / 2, 24, 'R', "#c8c6c4");
                }
                Group(sb, part[c]);
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(y + r)}\" r=\"{F(r)}\" fill=\"{col}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                Text(sb, cx - r * 0.75, y + r * 0.35, r * 1.5, r * 1.3, part[c].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 2);
                double ky = y + r * 2 + 10;
                for (int k = 0; k < part[c].Children.Count; k++)
                {
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(ky)}\" width=\"{F(cw)}\" height=\"{F(kidH[c][k])}\" rx=\"6\" fill=\"{Tint(col, 0.84)}\"/>");
                    Text(sb, x, ky, cw, kidH[c][k], part[c].Children[k].Text, part[c].Children[k].Bullets, Ink, maxFs: 13, inset: 6);
                    ky += kidH[c][k] + 6;
                }
                EndGroup(sb);
                tallest = Math.Max(tallest, ky - 6 - y);
            }
            return tallest;
        });

        // ------------------------------------------------------------------ block lists

        /// <summary>Square Accent List: a small square over each heading's rule, a square bullet by each line.</summary>
        private static (double, double) DrawSquareAccentList(StringBuilder sb, List<Item> items) => InColumns(items, 4, 26, (part, first, y, cw) =>
        {
            double headH = Math.Max(40, part.Max(i => Measure(i.Text, Array.Empty<string>(), cw - 10, 16)) + 8);
            double tallest = 0;
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 26);
                string col = Accent(first + c);
                Group(sb, part[c]);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"22\" height=\"22\" fill=\"{col}\"/>");
                Text(sb, x, y + 26, cw, headH, part[c].Text, Array.Empty<string>(), Ink, maxFs: 16, alignLeft: true, inset: 0);
                double ly = y + 26 + headH;
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(ly)}\" width=\"{F(cw)}\" height=\"3\" fill=\"{col}\"/>");
                ly += 10;
                foreach (var b in part[c].Bullets)
                {
                    double bh = Measure("", new[] { b }, cw - 20, 13) + 4;
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(ly + 7)}\" width=\"8\" height=\"8\" fill=\"{Tint(col, 0.35)}\"/>");
                    Text(sb, x + 14, ly, cw - 14, bh, "", new[] { b }, Ink, maxFs: 13, alignLeft: true, inset: 0);
                    ly += bh + 4;
                }
                EndGroup(sb);
                tallest = Math.Max(tallest, ly - y);
            }
            return tallest;
        });

        /// <summary>Alternating Hexagons: hexagons in a zigzag, each item's detail on the side its hexagon leans away from.</summary>
        private static (double, double) DrawAlternatingHexagons(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            // Pointy-topped hexagons tessellate half a width across and one and a half sides down.
            double s = 64, stepX = Math.Sqrt(3) * s * 0.5 + 4, drop = s * 1.5 + 4, colW = Math.Max(stepX * 2, 140);
            int perRow = Math.Max(1, (int)((BaseW - Pad * 2 - 2 * s) / stepX) + 1);
            perRow = Math.Min(perRow, 8);
            double bodyH = items.Any(i => i.Children.Count > 0) ? Math.Max(40, items.Max(i => Measure("", i.Bullets, colW - 10, 12)) + 8) : 0;
            double rowH = s * 2 + drop + bodyH * 2 + 20, y = Pad + bodyH;
            for (int start = 0; start < n; start += perRow)
            {
                int cnt = Math.Min(perRow, n - start);
                double x0 = (BaseW - (cnt - 1) * stepX) / 2;
                for (int k = 0; k < cnt; k++)
                {
                    int i = start + k;
                    bool low = k % 2 == 1;
                    double cx = x0 + k * stepX, cy = y + s + (low ? drop : 0);
                    Group(sb, items[i]);
                    sb.Append($"<polygon points=\"{HexPoints(cx, cy, s)}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                    Text(sb, cx - s * 0.78, cy - s * 0.55, s * 1.56, s * 1.1, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 13, inset: 2);
                    if (items[i].Children.Count > 0)
                    {
                        double by = low ? cy + s + 6 : cy - s - 6 - bodyH;
                        Text(sb, cx - colW / 2, by, colW, bodyH, "", items[i].Bullets, Ink, maxFs: 12, inset: 4);
                    }
                    EndGroup(sb);
                }
                y += rowH;
            }
            return (BaseW, y - rowH + s * 2 + drop + bodyH + Pad);
        }
    }
}
