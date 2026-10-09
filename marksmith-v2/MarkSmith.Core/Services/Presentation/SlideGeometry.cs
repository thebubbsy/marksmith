using System;
using System.Linq;

namespace MarkSmith.Services.Presentation;

/// <summary>
/// Slide geometry and text metrics shared by the paginator and the PowerPoint writer, so the
/// heights used to decide where a slide breaks are the heights the shapes are drawn at.
///
/// The measurements are estimates (no font shaping): average glyph widths for Calibri and
/// Consolas, rounded up so text that is close to the edge moves to the next slide rather
/// than spilling past the bottom.
/// </summary>
public static class SlideGeometry
{
    public const long EmuPerPt = 12700;

    // 16:9, 13.333 in x 7.5 in.
    public const long SlideWidth = 12192000;
    public const long SlideHeight = 6858000;

    public const double MarginXPt = 48;                  // 0.667 in
    public const double ContentWidthPt = SlideWidth / (double)EmuPerPt - 2 * MarginXPt;  // 864
    public const double TitleTopPt = 26;
    public const double TitleHeightPt = 70;
    public const double AccentBarTopPt = 100;
    public const double BodyTopPt = 116;
    public const double BodyBottomPt = 486;              // footer starts at 498
    public const double BodyHeightPt = BodyBottomPt - BodyTopPt; // 370
    public const double FooterTopPt = 500;
    public const double BlockGapPt = 12;

    public const double BodyFontPt = 20;
    public const double NestedFontPt = 18;
    public const double CaptionFontPt = 14;
    public const double MathFontPt = 24;
    public const double CodeFontPt = 14;
    public const double TableFontPt = 14;
    public const double TableDenseFontPt = 12;
    public const double LineSpacing = 1.2;
    public const double ParagraphGapPt = 8;

    public const double PanelPadXPt = 16;
    public const double PanelPadYPt = 10;
    public const double CodePadXPt = 14;
    public const double CodePadYPt = 10;
    public const double CellPadXPt = 6;
    public const double CellPadYPt = 4;

    /// <summary>Bullet indent: the text of a level-n item starts at BulletIndent * (n + 1).</summary>
    public const double BulletIndentPt = 26;

    public static long Emu(double pt) => (long)Math.Round(pt * EmuPerPt);

    public static double FontSize(SlideParagraph p) => p.Style switch
    {
        ParaStyle.Subheading => p.HeadingLevel <= 3 ? 24 : 21,
        ParaStyle.Caption => CaptionFontPt,
        ParaStyle.Math => MathFontPt,
        ParaStyle.Body => BodyFontPt,
        _ => p.Level == 0 ? BodyFontPt : NestedFontPt,
    };

    public static double LeftIndent(SlideParagraph p) => p.Style switch
    {
        ParaStyle.Bullet or ParaStyle.Numbered or ParaStyle.Task or ParaStyle.Continuation
            => BulletIndentPt * (p.Level + 1) + (p.Style == ParaStyle.Numbered ? 6 : 0),
        _ => 0,
    };

    /// <summary>Lines <paramref name="text"/> wraps to in a box <paramref name="widthPt"/> wide.</summary>
    public static int WrappedLines(string text, double fontPt, double widthPt, bool mono = false)
    {
        var perChar = fontPt * (mono ? 0.6 : 0.52);
        var perLine = Math.Max(1, (int)Math.Floor(widthPt / perChar));
        int lines = 0;
        foreach (var segment in text.Split('\n'))
        {
            var len = WidthUnits(segment);
            lines += Math.Max(1, (int)Math.Ceiling(len / perLine));
        }
        return Math.Max(1, lines);
    }

    // Wide glyphs (CJK, emoji) take about two average Latin widths.
    private static double WidthUnits(string s)
    {
        double n = 0;
        foreach (var ch in s)
        {
            if (char.IsLowSurrogate(ch)) continue;
            n += ch >= 0x2E80 || char.IsHighSurrogate(ch) ? 2 : ch is 'm' or 'w' or 'M' or 'W' ? 1.4 : 1;
        }
        return n;
    }

    public static double ParagraphHeight(SlideParagraph p, double widthPt, double scale = 1)
    {
        var size = FontSize(p) * scale;
        var lines = WrappedLines(p.PlainText, size, widthPt - LeftIndent(p), mono: false);
        return lines * size * LineSpacing;
    }

    public static double ParagraphSpaceBefore(SlideParagraph p, bool first) =>
        first ? 0 : p.Style == ParaStyle.Subheading ? 14 : p.Style == ParaStyle.Continuation ? 2 : ParagraphGapPt;

    public static double TextWidth(TextBlock b) =>
        b.Panel == PanelKind.None ? ContentWidthPt : ContentWidthPt - 2 * PanelPadXPt - 4;

    public static double TextHeight(TextBlock b) =>
        TextHeight(b.Paragraphs, b.Panel, TextWidth(b), b.FontScale);

    public static double TextHeight(System.Collections.Generic.IReadOnlyList<SlideParagraph> paras, PanelKind panel, double widthPt, double scale = 1)
    {
        double h = 0;
        for (int i = 0; i < paras.Count; i++)
            h += ParagraphSpaceBefore(paras[i], i == 0) * scale + ParagraphHeight(paras[i], widthPt, scale);
        if (panel != PanelKind.None) h += 2 * PanelPadYPt;
        return h;
    }

    public static double CodeFont(CodeSlideBlock c) => CodeFontPt;

    public static double CodeLineHeight(CodeSlideBlock c) => CodeFont(c) * LineSpacing;

    public static int CodeLineRows(string line, double fontPt) =>
        WrappedLines(line.Replace("\t", "    "), fontPt, ContentWidthPt - 2 * CodePadXPt, mono: true);

    public static double CaptionHeight(string? caption) =>
        string.IsNullOrEmpty(caption) ? 0 : WrappedLines(caption, CaptionFontPt, ContentWidthPt) * CaptionFontPt * LineSpacing + 4;

    public static double CodeHeight(CodeSlideBlock c, int from, int count)
    {
        var font = CodeFont(c);
        int rows = 0;
        for (int i = from; i < from + count && i < c.Lines.Count; i++) rows += CodeLineRows(c.Lines[i], font);
        return CaptionHeight(c.Caption) + rows * CodeLineHeight(c) + 2 * CodePadYPt;
    }

    public static double TableFont(TableSlideBlock t) => t.Columns > 5 ? TableDenseFontPt : TableFontPt;

    public static double CellHeight(System.Collections.Generic.IReadOnlyList<SlideParagraph> cell, double colWidthPt, double fontPt)
    {
        double h = 0;
        foreach (var p in cell)
            h += WrappedLines(p.PlainText, fontPt, Math.Max(20, colWidthPt - 2 * CellPadXPt)) * fontPt * LineSpacing;
        return Math.Max(fontPt * LineSpacing, h) + 2 * CellPadYPt;
    }

    public static double RowHeight(TableSlideBlock t, System.Collections.Generic.IReadOnlyList<System.Collections.Generic.List<SlideParagraph>> row)
    {
        var font = TableFont(t);
        double h = 0;
        for (int c = 0; c < row.Count && c < t.Widths.Count; c++)
            h = Math.Max(h, CellHeight(row[c], t.Widths[c] * ContentWidthPt, font));
        return h == 0 ? font * LineSpacing + 2 * CellPadYPt : h;
    }

    /// <summary>The size a picture is drawn at when <paramref name="maxHeightPt"/> is available.</summary>
    public static (double W, double H) FitPicture(PictureSlideBlock p, double maxHeightPt)
    {
        var w = Math.Max(1, p.NaturalWidthPt);
        var h = Math.Max(1, p.NaturalHeightPt);
        var captionH = CaptionHeight(p.Caption);
        var availH = Math.Max(20, maxHeightPt - captionH);
        var grow = p.IsDiagram ? 1.5 : 1.0;
        var scale = new[] { grow, ContentWidthPt / w, availH / h }.Min();
        return (w * scale, h * scale);
    }

    public const double MissingPictureHeightPt = 64;

    /// <summary>Title text size: long titles step down so they stay inside the title band.</summary>
    public static double TitleFont(string title)
    {
        foreach (var size in new[] { 32.0, 28, 24 })
            if (WrappedLines(title, size, ContentWidthPt) * size * 1.1 <= TitleHeightPt) return size;
        return 20;
    }
}
