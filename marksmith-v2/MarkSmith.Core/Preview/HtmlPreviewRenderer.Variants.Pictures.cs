using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MarkSmith.Core.Preview
{
    // Run #52: the last layouts that shared a gallery tile. Thirty picture layouts drew as eleven
    // pictures (Meet the Team, Meet the Team Oval, Bubble Picture List and Alternating Picture
    // Circles were all the same row of circles), and the four horizontal hierarchies were one tree.
    // Each drawing follows the shapes in the layout's own definition: tabs (round2SameRect) for
    // Bending Picture Accent List, a half frame for Framed Text Picture, donuts for Bubble Picture
    // List, connector dots for Alternating Picture Circles, rotated text for Picture Accent Blocks.
    public static partial class HtmlPreviewRenderer
    {
        // ------------------------------------------------------------------ shared

        /// <summary>Lays <paramref name="n"/> cards of width <paramref name="cw"/> into centred
        /// rows; calls <paramref name="draw"/> with (index, x, y) and returns the bottom.</summary>
        private static double CardRows(int n, int cols, double cw, double gap, double y, double rowH, Action<int, double, double> draw)
        {
            for (int row = 0; row * cols < n; row++)
            {
                int count = Math.Min(cols, n - row * cols);
                double x0 = (BaseW - (count * cw + (count - 1) * gap)) / 2;
                for (int c = 0; c < count; c++) draw(row * cols + c, x0 + c * (cw + gap), y);
                y += rowH + gap;
            }
            return y - gap;
        }

        /// <summary>A box with only its bottom corners rounded: the lower half of a picture card.</summary>
        private static string BottomTabD(double x, double y, double w, double h, double r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2);
            return $"M{F(x)} {F(y)}H{F(x + w)}V{F(y + h - r)}A{F(r)} {F(r)} 0 0 1 {F(x + w - r)} {F(y + h)}H{F(x + r)}A{F(r)} {F(r)} 0 0 1 {F(x)} {F(y + h - r)}Z";
        }

        private static double CaptionHeight(List<Item> items, double w, double min = 40, double fs = 13) =>
            Math.Max(min, items.Max(i => Measure(i.Text, i.Bullets, w, fs)) + 12);

        // ------------------------------------------------------------------ picture blocks

        /// <summary>Bending Picture Accent List: a picture with a tab-shaped caption hanging under
        /// its lower edge, offset to the right, and a round accent where the two meet.</summary>
        private static (double, double) DrawPictureTabCaption(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = PicCols(n, 3);
            double gap = 28, cw = Math.Min(220, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), ph = cw * 0.72;
            double capH = CaptionHeight(items, cw - 40, 44);
            double bottom = CardRows(n, cols, cw, gap, Pad, ph + capH - 8, (i, x, y) =>
            {
                string c = Accent(i);
                Group(sb, items[i]);
                Picture(sb, x, y, cw, ph);
                sb.Append($"<path d=\"{TabD(x + 22, y + ph - 8, cw - 22, capH, 12)}\" fill=\"{c}\"/>");
                sb.Append($"<circle cx=\"{F(x + 22)}\" cy=\"{F(y + ph - 8)}\" r=\"13\" fill=\"{Tint(c, 0.45)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                Text(sb, x + 22, y + ph - 4, cw - 22, capH - 4, items[i].Text, items[i].Bullets, "#ffffff", maxFs: 14, alignLeft: true, inset: 14);
                EndGroup(sb);
            });
            return (BaseW, bottom + Pad);
        }

        /// <summary>Titled Picture Blocks: a title bar over each block, then the picture with the
        /// item's text in a panel beside it.</summary>
        private static (double, double) DrawTitledPictureBlocks(StringBuilder sb, List<Item> items)
        {
            double y = Pad, w = BaseW - Pad * 2, titleH = 38, pw = 220, gap = 12;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double bodyH = Math.Max(pw * 0.6, Measure("", it.Bullets, w - pw - gap - 28) + 24);
                Group(sb, it);
                sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(titleH)}\" rx=\"3\" fill=\"{c}\"/>");
                Text(sb, Pad, y, w, titleH, it.Text, Array.Empty<string>(), "#ffffff", maxFs: 15, alignLeft: true, inset: 14);
                Picture(sb, Pad, y + titleH + 6, pw, bodyH);
                sb.Append($"<rect x=\"{F(Pad + pw + gap)}\" y=\"{F(y + titleH + 6)}\" width=\"{F(w - pw - gap)}\" height=\"{F(bodyH)}\" rx=\"3\" fill=\"{Tint(c, 0.85)}\"/>");
                if (it.Bullets.Count > 0)
                    Text(sb, Pad + pw + gap, y + titleH + 6, w - pw - gap, bodyH, "", it.Bullets, Ink, maxFs: 14, alignLeft: true, inset: 14);
                EndGroup(sb);
                y += titleH + 6 + bodyH + 18;
            }
            return (BaseW, y - 18 + Pad);
        }

        /// <summary>Picture Accent Blocks: pictures in blocks, each with its text running up a
        /// coloured strip down its left edge.</summary>
        private static (double, double) DrawPictureAccentBlocks(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = PicCols(n, 3);
            double gap = 14, strip = 46, cw = Math.Min(250, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), ph = (cw - strip) * 0.95;
            double bottom = CardRows(n, cols, cw, gap, Pad, ph, (i, x, y) =>
            {
                Group(sb, items[i]);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(strip)}\" height=\"{F(ph)}\" fill=\"{Accent(i)}\"/>");
                double cx = x + strip / 2, cy = y + ph / 2;
                // The text reads bottom to top, as Word draws it.
                sb.Append($"<g transform=\"rotate(-90 {F(cx)} {F(cy)})\">");
                Text(sb, cx - ph / 2, cy - strip / 2, ph, strip, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 8);
                sb.Append("</g>");
                Picture(sb, x + strip + 3, y, cw - strip - 3, ph);
                EndGroup(sb);
            });
            return (BaseW, bottom + Pad);
        }

        // ------------------------------------------------------------------ frames

        /// <summary>Snapshot Picture List: a framed snapshot on the left of each row, the item's
        /// title and text beside it.</summary>
        private static (double, double) DrawSnapshotRows(StringBuilder sb, List<Item> items)
        {
            double y = Pad, pw = 170, border = 9, tx = Pad + pw + 28, tw = BaseW - Pad - tx;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                double rh = Math.Max(pw * 0.72, Measure(it.Text, it.Bullets, tw - 10) + 26);
                Group(sb, it);
                sb.Append($"<rect x=\"{F(Pad + 4)}\" y=\"{F(y + 5)}\" width=\"{F(pw)}\" height=\"{F(rh)}\" fill=\"#000000\" fill-opacity=\"0.12\"/>");
                sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y)}\" width=\"{F(pw)}\" height=\"{F(rh)}\" fill=\"#ffffff\" stroke=\"#c8c6c4\"/>");
                Picture(sb, Pad + border, y + border, pw - border * 2, rh - border * 2);
                // The accent dash sits on top of the text block, wherever the block centres.
                double th = Measure(it.Text, it.Bullets, tw - 10) + 8, ty = y + (rh - th) / 2 + 6;
                sb.Append($"<rect x=\"{F(tx + 2)}\" y=\"{F(ty - 10)}\" width=\"36\" height=\"4\" fill=\"{Accent(i)}\"/>");
                Text(sb, tx, ty, tw, th, it.Text, it.Bullets, Ink, maxFs: 15, alignLeft: true, inset: 2);
                EndGroup(sb);
                y += rh + 20;
            }
            return (BaseW, y - 20 + Pad);
        }

        /// <summary>Framed Text Picture: a large picture with the text on a panel over its lower
        /// right corner, held by a half frame (two sides of a square) in the accent colour.</summary>
        private static (double, double) DrawHalfFramePictures(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = PicCols(n, 3);
            double gap = 30, cw = Math.Min(240, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), ph = cw * 0.78;
            double tw = cw * 0.72, capH = CaptionHeight(items, tw - 24, 46);
            double rowH = ph + capH * 0.55;
            double bottom = CardRows(n, cols, cw, gap, Pad, rowH, (i, x, y) =>
            {
                string c = Accent(i);
                double bx = x + cw - tw + 10, by = y + ph - capH * 0.45;
                Group(sb, items[i]);
                Picture(sb, x, y, cw - 10, ph);
                sb.Append($"<rect x=\"{F(bx)}\" y=\"{F(by)}\" width=\"{F(tw)}\" height=\"{F(capH)}\" fill=\"{Tint(c, 0.8)}\"/>");
                sb.Append($"<path d=\"M{F(bx - 7)} {F(by + capH * 0.7)}V{F(by - 7)}H{F(bx + tw * 0.7)}\" fill=\"none\" stroke=\"{c}\" stroke-width=\"7\"/>");
                Text(sb, bx, by, tw, capH, items[i].Text, items[i].Bullets, Ink, maxFs: 14, alignLeft: true, inset: 12);
                EndGroup(sb);
            });
            return (BaseW, bottom + Pad);
        }

        // ------------------------------------------------------------------ captions

        /// <summary>Bending Picture Caption: the caption on a dark band that straddles the
        /// picture's lower edge and hangs out past its left side.</summary>
        private static (double, double) DrawPictureCaptionOverlap(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = PicCols(n, 3);
            double gap = 34, cw = Math.Min(230, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), ph = cw * 0.75;
            double bw = cw * 0.8, capH = CaptionHeight(items, bw - 24, 42);
            double bottom = CardRows(n, cols, cw, gap, Pad, ph + capH * 0.5, (i, x, y) =>
            {
                Group(sb, items[i]);
                Picture(sb, x + 12, y, cw - 12, ph);
                double by = y + ph - capH * 0.5;
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(by)}\" width=\"{F(bw)}\" height=\"{F(capH)}\" fill=\"{Ink}\" fill-opacity=\"0.82\"/>");
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(by)}\" width=\"5\" height=\"{F(capH)}\" fill=\"{Accent(i)}\"/>");
                Text(sb, x + 5, by, bw - 5, capH, items[i].Text, items[i].Bullets, "#ffffff", maxFs: 14, alignLeft: true, inset: 10);
                EndGroup(sb);
            });
            return (BaseW, bottom + Pad);
        }

        /// <summary>Picture Caption List: each picture and its caption together on one outlined
        /// card.</summary>
        private static (double, double) DrawPictureCaptionCards(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = PicCols(n, 4);
            double gap = 18, cw = Math.Min(210, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), inner = 10, ph = (cw - inner * 2) * 0.75;
            double capH = CaptionHeight(items, cw - inner * 2 - 8, 40);
            double ch = inner + ph + capH + inner;
            double bottom = CardRows(n, cols, cw, gap, Pad, ch, (i, x, y) =>
            {
                string c = Accent(i);
                Group(sb, items[i]);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(ch)}\" rx=\"10\" fill=\"#ffffff\" stroke=\"{c}\" stroke-width=\"2\"/>");
                Picture(sb, x + inner, y + inner, cw - inner * 2, ph);
                Text(sb, x + inner, y + inner + ph, cw - inner * 2, capH, items[i].Text, items[i].Bullets, Ink, maxFs: 14, inset: 4);
                EndGroup(sb);
            });
            return (BaseW, bottom + Pad);
        }

        /// <summary>Horizontal Picture List: pictures in a row, each joined to a coloured text
        /// block underneath with rounded lower corners.</summary>
        private static (double, double) DrawPictureOverBlock(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = PicCols(n, 4);
            double gap = 14, cw = Math.Min(200, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), ph = cw * 0.9;
            double capH = CaptionHeight(items, cw - 24, 56);
            double bottom = CardRows(n, cols, cw, gap, Pad, ph + capH, (i, x, y) =>
            {
                Group(sb, items[i]);
                sb.Append($"<path d=\"{TabD(x, y, cw, ph, 12)}\" fill=\"#edebe9\"/>");
                Picture(sb, x + 6, y + 6, cw - 12, ph - 6);
                sb.Append($"<path d=\"{BottomTabD(x, y + ph, cw, capH, 12)}\" fill=\"{Accent(i)}\"/>");
                Text(sb, x, y + ph, cw, capH, items[i].Text, items[i].Bullets, "#ffffff", maxFs: 14, inset: 10);
                EndGroup(sb);
            });
            return (BaseW, bottom + Pad);
        }

        /// <summary>Picture Strips: long text strips in two columns, a square picture overlapping
        /// each strip's left end and standing proud of it.</summary>
        private static (double, double) DrawPictureStrips(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = n == 1 ? 1 : 2;
            double gap = 26, cw = (BaseW - Pad * 2 - (cols - 1) * gap) / cols, ps = 84, sh = 64;
            double y = Pad + 10;
            for (int row = 0; row * cols < n; row++)
            {
                double rh = Math.Max(sh, items.Skip(row * cols).Take(cols).Max(it => Measure(it.Text, it.Bullets, cw - ps - 30) + 18));
                for (int c = 0; c < cols && row * cols + c < n; c++)
                {
                    int i = row * cols + c;
                    double x = Pad + c * (cw + gap);
                    Group(sb, items[i]);
                    sb.Append($"<rect x=\"{F(x + 10)}\" y=\"{F(y)}\" width=\"{F(cw - 10)}\" height=\"{F(rh)}\" fill=\"#ffffff\" stroke=\"{Tint(Accent(i), 0.35)}\" stroke-width=\"2\"/>");
                    sb.Append($"<rect x=\"{F(x - 3)}\" y=\"{F(y - 13)}\" width=\"{F(ps + 6)}\" height=\"{F(ps + 6)}\" fill=\"{Accent(i)}\"/>");
                    Picture(sb, x, y - 10, ps, ps);
                    Text(sb, x + ps + 10, y, cw - ps - 10, rh, items[i].Text, items[i].Bullets, Ink, maxFs: 14, alignLeft: true, inset: 12);
                    EndGroup(sb);
                }
                y += Math.Max(rh, ps - 10) + gap;
            }
            return (BaseW, y - gap + Pad);
        }

        /// <summary>Picture Accent List: each top-level item is a header bar with a picture at its
        /// left end; its children are listed under it, each with a small picture of its own.</summary>
        private static (double, double) DrawPictureAccentList(StringBuilder sb, List<Item> items)
        {
            double y = Pad, w = BaseW - Pad * 2, headH = 62, rowH = 46, indent = 44;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                sb.Append("<g class=\"sa-s\">").Append(Tooltip(new Item { Text = it.Text }));
                sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(headH)}\" rx=\"8\" fill=\"{c}\"/>");
                Picture(sb, Pad + 7, y + 7, headH - 14, headH - 14);
                Text(sb, Pad + headH, y, w - headH, headH, it.Text, Array.Empty<string>(), "#ffffff", maxFs: 16, alignLeft: true, inset: 10);
                sb.Append("</g>");
                y += headH + 8;
                foreach (var kid in it.Children)
                {
                    double kh = Math.Max(rowH, Measure(kid.Text, kid.Bullets, w - indent - rowH - 24, 13) + 12);
                    Group(sb, kid);
                    sb.Append($"<rect x=\"{F(Pad + indent)}\" y=\"{F(y)}\" width=\"{F(w - indent)}\" height=\"{F(kh)}\" rx=\"6\" fill=\"{Tint(c, 0.85)}\"/>");
                    Picture(sb, Pad + indent + 5, y + 5, rowH - 10, kh - 10);
                    Text(sb, Pad + indent + rowH, y, w - indent - rowH, kh, kid.Text, kid.Bullets, Ink, maxFs: 14, alignLeft: true, inset: 8);
                    EndGroup(sb);
                    y += kh + 6;
                }
                y += 12;
            }
            return (BaseW, y - 18 + Pad);
        }

        // ------------------------------------------------------------------ theme layouts

        /// <summary>Theme Picture Accent: the first item as one large picture with its caption
        /// band; the rest stacked as smaller pictures down the right.</summary>
        private static (double, double) DrawThemePictureAccent(StringBuilder sb, List<Item> items)
        {
            double h = 330, gap = 8, bigW = (BaseW - Pad * 2) * 0.6, x = Pad, y = Pad;
            var first = items[0];
            double capH = Math.Min(90, Math.Max(48, Measure(first.Text, first.Bullets, bigW - 28) + 14));
            Group(sb, first);
            Picture(sb, x, y, bigW, h);
            sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + h - capH)}\" width=\"{F(bigW)}\" height=\"{F(capH)}\" fill=\"{Accent(0)}\"/>");
            Text(sb, x, y + h - capH, bigW, capH, first.Text, first.Bullets, "#ffffff", maxFs: 16, alignLeft: true, inset: 14);
            EndGroup(sb);
            var rest = items.Skip(1).Take(4).ToList();
            double rx = x + bigW + gap, rw = BaseW - Pad - rx;
            double rh = rest.Count == 0 ? 0 : (h - (rest.Count - 1) * gap) / rest.Count;
            for (int i = 0; i < rest.Count; i++)
            {
                double ry = y + i * (rh + gap), band = Math.Min(rh * 0.42, 34);
                Group(sb, rest[i]);
                Picture(sb, rx, ry, rw, rh);
                sb.Append($"<rect x=\"{F(rx)}\" y=\"{F(ry + rh - band)}\" width=\"{F(rw)}\" height=\"{F(band)}\" fill=\"{Accent(i + 1)}\" fill-opacity=\"0.9\"/>");
                Text(sb, rx, ry + rh - band, rw, band, rest[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 13, alignLeft: true, inset: 8);
                EndGroup(sb);
            }
            double total = y + h;
            if (items.Count > 5) { Note(sb, BaseW / 2, total + 16, items.Count - 5); total += 18; }
            return (BaseW, total + Pad);
        }

        /// <summary>Theme Picture Grid: the first item's text on a coloured block in the top-left
        /// cell of a grid, a wide picture beside it and a row of pictures below.</summary>
        private static (double, double) DrawThemePictureGrid(StringBuilder sb, List<Item> items)
        {
            double gap = 6, cell = (BaseW - Pad * 2 - gap * 2) / 3, ch = cell * 0.7, x = Pad, y = Pad;
            var first = items[0];
            Group(sb, first);
            sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cell)}\" height=\"{F(ch)}\" fill=\"{Accent(0)}\"/>");
            Text(sb, x, y, cell, ch, first.Text, first.Bullets, "#ffffff", maxFs: 18, alignLeft: true, inset: 16);
            EndGroup(sb);
            var rest = items.Skip(1).ToList();
            // Cell 1 is the wide one (two columns); then rows of three.
            var slots = new List<(double x, double y, double w)> { (x + cell + gap, y, cell * 2 + gap) };
            for (int k = 0; slots.Count < rest.Count; k++)
                slots.Add((x + (k % 3) * (cell + gap), y + (k / 3 + 1) * (ch + gap), cell));
            double bottom = y + ch;
            for (int i = 0; i < rest.Count; i++)
            {
                var (sx, sy, sw) = slots[i];
                double band = 32;
                Group(sb, rest[i]);
                Picture(sb, sx, sy, sw, ch);
                sb.Append($"<rect x=\"{F(sx)}\" y=\"{F(sy + ch - band)}\" width=\"{F(sw)}\" height=\"{F(band)}\" fill=\"{Ink}\" fill-opacity=\"0.55\"/>");
                Text(sb, sx, sy + ch - band, sw, band, rest[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 13, alignLeft: true, inset: 8);
                EndGroup(sb);
                bottom = Math.Max(bottom, sy + ch);
            }
            return (BaseW, bottom + Pad);
        }

        /// <summary>Theme Picture Alternating Accent: columns of picture and coloured text block
        /// that swap places from one column to the next, so the blocks form a checkerboard.</summary>
        private static (double, double) DrawPictureCheckerboard(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 5);
            double cw = (BaseW - Pad * 2) / n, bh = Math.Min(200, cw * 0.85);
            double textH = Math.Max(bh, items.Take(n).Max(i => Measure(i.Text, i.Bullets, cw - 24, 13)) + 20);
            for (int i = 0; i < n; i++)
            {
                double x = Pad + i * cw;
                bool picFirst = i % 2 == 0;
                double py = picFirst ? Pad : Pad + textH, ty = picFirst ? Pad + bh : Pad;
                Group(sb, items[i]);
                Picture(sb, x, py, cw, bh);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(ty)}\" width=\"{F(cw)}\" height=\"{F(textH)}\" fill=\"{Accent(i)}\"/>");
                Text(sb, x, ty, cw, textH, items[i].Text, items[i].Bullets, "#ffffff", maxFs: 14, inset: 12);
                EndGroup(sb);
            }
            double h = Pad + bh + textH;
            if (items.Count > n) { Note(sb, BaseW / 2, h + 16, items.Count - n); h += 18; }
            return (BaseW, h + Pad);
        }

        // ------------------------------------------------------------------ portraits

        /// <summary>Meet the Team Oval: tall rounded portraits in an accent frame, names below.</summary>
        private static (double, double) DrawOvalPortraits(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = PicCols(n);
            double gap = 22, cw = Math.Min(170, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), pw = cw * 0.8, ph = pw * 1.3;
            double capH = CaptionHeight(items, cw - 12, 40);
            double bottom = CardRows(n, cols, cw, gap, Pad, ph + 12 + capH, (i, x, y) =>
            {
                double px = x + (cw - pw) / 2;
                Group(sb, items[i]);
                sb.Append($"<rect x=\"{F(px - 5)}\" y=\"{F(y - 5)}\" width=\"{F(pw + 10)}\" height=\"{F(ph + 10)}\" rx=\"{F(pw * 0.5)}\" fill=\"{Accent(i)}\"/>");
                sb.Append($"<rect x=\"{F(px)}\" y=\"{F(y)}\" width=\"{F(pw)}\" height=\"{F(ph)}\" rx=\"{F(pw * 0.45)}\" fill=\"#edebe9\"/>");
                Picture(sb, px + pw * 0.15, y + ph * 0.2, pw * 0.7, ph * 0.6);
                Text(sb, x, y + ph + 12, cw, capH, items[i].Text, items[i].Bullets, Ink, maxFs: 14, inset: 4);
                EndGroup(sb);
            });
            return (BaseW, bottom + Pad);
        }

        /// <summary>Bubble Picture List: the first item as one large round picture in a donut
        /// ring; the rest as smaller bubbles down the right, each with its text beside it.</summary>
        private static (double, double) DrawBubblePictures(StringBuilder sb, List<Item> items)
        {
            double R = 120, cx = Pad + R + 14, cy = Pad + R + 14;
            var first = items[0];
            Group(sb, first);
            sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(R + 10)}\" fill=\"none\" stroke=\"{Accent(0)}\" stroke-width=\"12\"/>");
            Picture(sb, cx - R, cy - R, R * 2, R * 2, circle: true);
            double capW = R * 1.5, capH = Math.Max(40, Measure(first.Text, first.Bullets, capW - 16) + 10);
            sb.Append($"<rect x=\"{F(cx - capW / 2)}\" y=\"{F(cy + R * 0.35)}\" width=\"{F(capW)}\" height=\"{F(capH)}\" rx=\"{F(capH / 2)}\" fill=\"{Accent(0)}\"/>");
            Text(sb, cx - capW / 2, cy + R * 0.35, capW, capH, first.Text, first.Bullets, "#ffffff", maxFs: 15, inset: 10);
            EndGroup(sb);
            var rest = items.Skip(1).ToList();
            double r = 34, lx = cx + R + 60, y = Pad + 6, bottom = cy + R + 24;
            for (int i = 0; i < rest.Count; i++)
            {
                // The bubbles follow the big circle's curve instead of a straight column.
                double row = rest.Count == 1 ? 0 : (i / (double)(rest.Count - 1)) * 2 - 1;
                double bx = lx - 40 * (1 - row * row);
                double rh = Math.Max(r * 2 + 12, Measure(rest[i].Text, rest[i].Bullets, BaseW - Pad - bx - r - 20, 13) + 12);
                double by = y + rh / 2;
                Group(sb, rest[i]);
                sb.Append($"<circle cx=\"{F(bx)}\" cy=\"{F(by)}\" r=\"{F(r + 6)}\" fill=\"none\" stroke=\"{Accent(i + 1)}\" stroke-width=\"6\"/>");
                Picture(sb, bx - r, by - r, r * 2, r * 2, circle: true);
                Text(sb, bx + r + 14, y, BaseW - Pad - bx - r - 14, rh, rest[i].Text, rest[i].Bullets, Ink, maxFs: 14, alignLeft: true, inset: 2);
                EndGroup(sb);
                y += rh + 8;
                bottom = Math.Max(bottom, y);
            }
            return (BaseW, bottom + Pad);
        }

        /// <summary>Alternating Picture Circles: round pictures along a line joined by small
        /// connector dots, the text in boxes that alternate above and below.</summary>
        private static (double, double) DrawAlternatingPictureCircles(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 6);
            double step = (BaseW - Pad * 2) / n, r = Math.Min(62, step * 0.36), bw = Math.Min(step * 1.5, 220) - 10;
            double capH = Math.Max(48, items.Take(n).Max(i => Measure(i.Text, i.Bullets, bw - 20, 13)) + 14);
            double cy = Pad + capH + 18 + r;
            sb.Append($"<line x1=\"{F(Pad + step / 2)}\" y1=\"{F(cy)}\" x2=\"{F(BaseW - Pad - step / 2)}\" y2=\"{F(cy)}\" stroke=\"{Connector}\" stroke-width=\"2\"/>");
            for (int i = 0; i + 1 < n; i++)
                sb.Append($"<circle cx=\"{F(Pad + step * (i + 1))}\" cy=\"{F(cy)}\" r=\"7\" fill=\"{Accent(i)}\"/>");
            for (int i = 0; i < n; i++)
            {
                double cx = Pad + step * (i + 0.5);
                bool above = i % 2 == 1;
                double bx = Math.Clamp(cx - bw / 2, Pad, BaseW - Pad - bw);
                double by = above ? cy - r - 18 - capH : cy + r + 18;
                Group(sb, items[i]);
                sb.Append($"<line x1=\"{F(cx)}\" y1=\"{F(above ? by + capH : cy + r)}\" x2=\"{F(cx)}\" y2=\"{F(above ? cy - r : by)}\" stroke=\"{Accent(i)}\" stroke-width=\"2\"/>");
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r + 4)}\" fill=\"{Accent(i)}\"/>");
                Picture(sb, cx - r, cy - r, r * 2, r * 2, circle: true);
                sb.Append($"<rect x=\"{F(bx)}\" y=\"{F(by)}\" width=\"{F(bw)}\" height=\"{F(capH)}\" rx=\"4\" fill=\"{Accent(i)}\"/>");
                Text(sb, bx, by, bw, capH, items[i].Text, items[i].Bullets, "#ffffff", maxFs: 14, inset: 10);
                EndGroup(sb);
            }
            double h = cy + r + 18 + capH;
            if (items.Count > n) { Note(sb, BaseW / 2, h + 16, items.Count - n); h += 18; }
            return (BaseW, h + Pad);
        }

        /// <summary>Meet the Team Card Vertical: one wide card per person, stacked, the round
        /// portrait at the card's left and the name and details beside it.</summary>
        private static (double, double) DrawTeamCardRows(StringBuilder sb, List<Item> items)
        {
            double y = Pad, w = Math.Min(560, BaseW - Pad * 2), x = (BaseW - w) / 2, r = 40;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double ch = Math.Max(r * 2 + 24, Measure(it.Text, it.Bullets, w - r * 2 - 60) + 24);
                Group(sb, it);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(ch)}\" rx=\"10\" fill=\"#ffffff\" stroke=\"{Tint(c, 0.5)}\" stroke-width=\"1.5\"/>");
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(r * 2 + 36)}\" height=\"{F(ch)}\" rx=\"10\" fill=\"{Tint(c, 0.8)}\"/>");
                sb.Append($"<circle cx=\"{F(x + r + 18)}\" cy=\"{F(y + ch / 2)}\" r=\"{F(r + 4)}\" fill=\"{c}\"/>");
                Picture(sb, x + 18, y + ch / 2 - r, r * 2, r * 2, circle: true);
                Text(sb, x + r * 2 + 44, y, w - r * 2 - 44, ch, it.Text, it.Bullets, Ink, maxFs: 15, alignLeft: true, inset: 6);
                EndGroup(sb);
                y += ch + 12;
            }
            return (BaseW, y - 12 + Pad);
        }

        /// <summary>Title Picture Lineup: each picture headed by its own title bar, columns split by
        /// thin rules, the details under the picture.</summary>
        private static (double, double) DrawTitledLineup(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 6);
            double gap = 18, cw = (BaseW - Pad * 2 - (n - 1) * gap) / n, titleH = 40, ph = Math.Min(200, Math.Max(130, cw * 1.05));
            double bodyH = items.Take(n).Any(i => i.Bullets.Count > 0)
                ? Math.Max(30, items.Take(n).Max(i => Measure("", i.Bullets, cw - 8, 13)) + 10) : 0;
            double y = Pad;
            for (int i = 0; i < n; i++)
            {
                double x = Pad + i * (cw + gap);
                if (i > 0) sb.Append($"<line x1=\"{F(x - gap / 2)}\" y1=\"{F(y)}\" x2=\"{F(x - gap / 2)}\" y2=\"{F(y + titleH + ph + bodyH)}\" stroke=\"{Connector}\" stroke-width=\"1.5\"/>");
                Group(sb, items[i]);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(titleH)}\" fill=\"{Accent(i)}\"/>");
                Text(sb, x, y, cw, titleH, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 6);
                Picture(sb, x, y + titleH + 4, cw, ph);
                if (items[i].Bullets.Count > 0)
                    Text(sb, x, y + titleH + 8 + ph, cw, bodyH, "", items[i].Bullets, Ink, maxFs: 13, inset: 2);
                EndGroup(sb);
            }
            double h = y + titleH + 8 + ph + bodyH;
            if (items.Count > n) { Note(sb, BaseW / 2, h + 14, items.Count - n); h += 18; }
            return (BaseW, h + Pad);
        }

        // ------------------------------------------------------------------ horizontal hierarchies

        internal enum HTreeStyle { Org, MultiLevel, Labeled }

        /// <summary>The horizontal hierarchies beyond the plain one (Horizontal Hierarchy keeps
        /// <see cref="DrawTree"/>): Horizontal Organization Chart (square cards with a coloured
        /// edge), Horizontal Multi-Level Hierarchy (each item a bar as tall as everything under it)
        /// and Horizontal Labeled Hierarchy (a shaded, labelled band behind each level).</summary>
        private static (double, double) DrawHorizontalTree(StringBuilder sb, List<Item> items, HTreeStyle style)
        {
            var (roots, leaves, depth) = BuildTree(items);
            double slot = style == HTreeStyle.MultiLevel ? 56 : 64, top = Pad + (style == HTreeStyle.Labeled ? 40 : 0);
            double h = Math.Max(240, top + Pad + leaves * slot);
            double colW = (BaseW - Pad * 2) / Math.Max(1, depth);
            double boxW = Math.Min(190, colW - 36), boxH = 48;
            double oy = top + (h - top - Pad - leaves * slot) / 2;
            double X(TNode n) => Pad + n.Depth * colW + (colW - boxW) / 2;
            double Y(TNode n) => oy + n.Pos * slot;

            if (style == HTreeStyle.Labeled)
            {
                // Level bands: alternate shades, a label tab at the head of each.
                for (int d = 0; d < depth; d++)
                {
                    double bx = Pad + d * colW;
                    sb.Append($"<rect x=\"{F(bx + 2)}\" y=\"{F(Pad)}\" width=\"{F(colW - 4)}\" height=\"{F(h - Pad * 2)}\" rx=\"6\" fill=\"{Tint(LevelColors[0], d % 2 == 0 ? 0.9 : 0.82)}\"/>");
                    sb.Append($"<rect x=\"{F(bx + 2)}\" y=\"{F(Pad)}\" width=\"{F(colW - 4)}\" height=\"30\" rx=\"6\" fill=\"{Tint(LevelColors[0], 0.55)}\"/>");
                    sb.Append($"<text x=\"{F(bx + colW / 2)}\" y=\"{F(Pad + 20)}\" text-anchor=\"middle\" font-size=\"12\" font-weight=\"600\" fill=\"{Ink}\">Level {d + 1}</text>");
                }
            }

            var edges = new StringBuilder();
            var nodes = new StringBuilder();
            foreach (var n in All(roots))
            {
                string color = LevelColors[n.Depth % LevelColors.Length];
                if (style == HTreeStyle.MultiLevel)
                {
                    double bx = Pad + n.Depth * colW + 6, bw = colW - 12;
                    double by = oy + n.Start * slot + 4, bh = n.Leaves * slot - 8;
                    if (n.Kids.Count > 0)
                        edges.Append($"<path d=\"M{F(bx + bw)} {F(by + bh / 2)}H{F(bx + bw + 6)}\" stroke=\"{Connector}\" stroke-width=\"2\"/>");
                    Box(nodes, bx, by, bw, bh, color, n.Item, withBullets: false, rx: 2);
                    continue;
                }
                foreach (var k in n.Kids)
                {
                    double midX = X(n) + boxW + (colW - boxW) / 2;
                    edges.Append($"<path d=\"M{F(X(n) + boxW)} {F(Y(n))}H{F(midX)}V{F(Y(k))}H{F(X(k))}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"1.5\"/>");
                }
                if (style == HTreeStyle.Org)
                {
                    nodes.Append("<g class=\"sa-s\">").Append(Tooltip(n.Item));
                    nodes.Append($"<rect x=\"{F(X(n))}\" y=\"{F(Y(n) - boxH / 2)}\" width=\"{F(boxW)}\" height=\"{F(boxH)}\" fill=\"{Tint(color, 0.78)}\" stroke=\"{Tint(color, 0.45)}\" stroke-width=\"1.5\"/>");
                    nodes.Append($"<rect x=\"{F(X(n))}\" y=\"{F(Y(n) - boxH / 2)}\" width=\"8\" height=\"{F(boxH)}\" fill=\"{color}\"/>");
                    Text(nodes, X(n) + 8, Y(n) - boxH / 2, boxW - 8, boxH, n.Item.Text, Array.Empty<string>(), Ink, maxFs: 14, inset: 8);
                    nodes.Append("</g>");
                }
                else
                {
                    Box(nodes, X(n), Y(n) - boxH / 2, boxW, boxH, color, n.Item, withBullets: false);
                }
            }
            sb.Append(edges).Append(nodes);
            return (BaseW, h);
        }
    }
}
