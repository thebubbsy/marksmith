using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MarkSmith.Core.Preview;

namespace MarkSmith.Services;

/// <summary>
/// Everything behind Insert ▸ SmartArt that isn't WinUI: the layouts the dialog offers, the worked
/// examples, and turning the indented text box into a <c>:::smartart</c> block.
/// </summary>
/// <remarks>
/// The dialog used to offer four made-up type names ("process", "list", …) and trimmed every line,
/// so an "org chart" came out as four boxes in a row: indentation is what makes a hierarchy, and it
/// was thrown away. It now offers real Word layouts by the names Word's gallery uses (the same
/// aliases SmartArt Studio writes), and keeps indentation as nested bullets — sub-items are extra
/// shapes in a hierarchy, and the shape's bullet points in every other layout.
/// </remarks>
public static class SmartArtInsert
{
    /// <summary>One layout in the dialog's gallery.</summary>
    /// <param name="Alias">The token written after <c>type=</c>; one of Word's own layout ids.</param>
    /// <param name="Name">The name Word's SmartArt gallery shows for it.</param>
    /// <param name="Hint">One line on when to pick it (tooltip).</param>
    public sealed record Layout(string Alias, string Name, string Hint)
    {
        /// <summary>The drawing the preview uses; drives the gallery miniature.</summary>
        public SmartArtPreviewFamily Family => HtmlPreviewRenderer.ResolveFamily(Alias);

        /// <summary>The gallery miniature: the family's real shapes as SVG (no text).</summary>
        public string ThumbnailSvg => HtmlPreviewRenderer.RenderThumbnailSvg(Family);

        /// <summary>True when every level of the outline is a shape of its own, rather than the
        /// sub-items being bullet text inside their parent's shape.</summary>
        public bool NestsAsShapes => Family is SmartArtPreviewFamily.Hierarchy or SmartArtPreviewFamily.HorizontalHierarchy
            or SmartArtPreviewFamily.BlockHierarchy or SmartArtPreviewFamily.HierarchyList;
    }

    /// <summary>A worked example: a layout plus outline text that suits it.</summary>
    public sealed record Example(string Name, string Glyph, string Alias, string Text);

    /// <summary>The common layouts, in the order Word's gallery groups them (list, process, cycle,
    /// hierarchy, relationship, matrix, pyramid). The full catalog is in SmartArt Studio.</summary>
    public static IReadOnlyList<Layout> Layouts { get; } = new[]
    {
        new Layout("default", "Basic Block List", "Separate ideas of equal weight, as a grid of blocks."),
        new Layout("vList2", "Vertical Bullet List", "Headed groups, each with its own bullet points."),
        new Layout("process1", "Basic Process", "Steps in order, left to right."),
        new Layout("chevron1", "Basic Chevron Process", "Stages or phases that lead into each other."),
        new Layout("process2", "Vertical Process", "Steps in order, top to bottom. Suits longer step names."),
        new Layout("hProcess11", "Basic Timeline", "Dates or milestones along a line."),
        new Layout("cycle2", "Basic Cycle", "Stages that repeat, with no end."),
        new Layout("radial1", "Basic Radial", "A central idea and what relates to it. The first item is the centre."),
        new Layout("orgChart1", "Organization Chart", "Who reports to whom, from the top down."),
        new Layout("venn1", "Basic Venn", "Overlapping ideas or groups."),
        new Layout("matrix3", "Basic Matrix", "Four quadrants, such as a SWOT analysis."),
        new Layout("pyramid1", "Basic Pyramid", "Levels that build on each other, largest at the bottom."),
    };

    /// <summary>The layout the dialog opens on.</summary>
    public const string DefaultAlias = "process1";

    /// <summary>Starting points that show off what each kind of layout is for.</summary>
    public static IReadOnlyList<Example> Examples { get; } = new[]
    {
        new Example("Project phases", "", "chevron1", "Discover\nDesign\nBuild\nLaunch"),
        new Example("Plan, do, check, act", "", "cycle2", "Plan\n  Set goals and targets\nDo\n  Try the change\nCheck\n  Measure the results\nAct\n  Adopt what worked"),
        new Example("Team structure", "", "orgChart1", "Managing Director\n  Operations\n    Logistics\n    Facilities\n  Finance\n  Sales"),
        new Example("SWOT analysis", "", "matrix3", "Strengths\nWeaknesses\nOpportunities\nThreats"),
        new Example("Quarterly roadmap", "", "hProcess11", "Q1: Research\nQ2: Prototype\nQ3: Beta\nQ4: Launch"),
    };

    /// <summary>The layout with this alias, or null.</summary>
    public static Layout? Find(string? alias) =>
        Layouts.FirstOrDefault(l => string.Equals(l.Alias, alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>One outline row: its nesting level (0 = top) and its text.</summary>
    public readonly record struct Row(int Level, string Text);

    /// <summary>
    /// Reads indented text into outline rows. Lines may be indented with spaces or tabs and may
    /// already carry a list marker (<c>-</c>, <c>*</c>, <c>+</c>, <c>1.</c>), so a pasted Markdown
    /// list works as-is. A level never jumps by more than one, and the first line is always level 0,
    /// so the result is always a valid nested list. Blank lines are skipped. WinUI's TextBox
    /// separates lines with a bare '\r', which counts as a line break.
    /// </summary>
    public static IReadOnlyList<Row> Parse(string? text)
    {
        var rows = new List<Row>();
        var widths = new List<int>(); // indent width of each open level
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var body = raw.TrimStart(' ', '\t');
            var item = StripMarker(body.TrimEnd());
            if (item.Length == 0) continue;
            var width = 0;
            foreach (var c in raw)
            {
                if (c == ' ') width++;
                else if (c == '\t') width += 4;
                else break;
            }

            while (widths.Count > 0 && widths[^1] > width) widths.RemoveAt(widths.Count - 1);
            int level;
            if (widths.Count > 0 && widths[^1] == width) level = widths.Count - 1;
            else { level = widths.Count; widths.Add(width); }
            rows.Add(new Row(level, item));
        }
        return rows;
    }

    private static string StripMarker(string s)
    {
        if (s.Length >= 2 && s[0] is '-' or '*' or '+' && s[1] == ' ') return s[2..].Trim();
        var digits = 0;
        while (digits < s.Length && char.IsDigit(s[digits])) digits++;
        if (digits > 0 && digits + 1 < s.Length && s[digits] is '.' or ')' && s[digits + 1] == ' ')
            return s[(digits + 2)..].Trim();
        return s.Trim();
    }

    /// <summary>The <c>:::smartart</c> block for these rows; nesting is two spaces per level, the
    /// form the preview, DOCX export and SmartArt Studio all read. No rows gives the one-step
    /// placeholder, like the other insert builders.</summary>
    public static string Build(string? alias, IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder("\n:::smartart type=\"")
            .Append(string.IsNullOrWhiteSpace(alias) ? DefaultAlias : alias.Trim()).Append("\"\n");
        if (rows.Count == 0) sb.Append("- Step 1\n");
        foreach (var row in rows) sb.Append(' ', row.Level * 2).Append("- ").Append(row.Text).Append('\n');
        return sb.Append(":::\n").ToString();
    }

    public static string Build(string? alias, string? text) => Build(alias, Parse(text));

    /// <summary>The count shown above the text box, worded for how the layout uses sub-items:
    /// "4 shapes", "4 shapes · 4 sub-points", or "6 boxes in 3 levels" for a hierarchy.</summary>
    public static string Describe(Layout? layout, IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return "No items";
        if (layout?.NestsAsShapes == true)
        {
            var levels = rows.Max(r => r.Level) + 1;
            return $"{Plural(rows.Count, "box", "boxes")}" + (levels > 1 ? $" in {levels} levels" : "");
        }
        var shapes = rows.Count(r => r.Level == 0);
        var subs = rows.Count - shapes;
        return Plural(shapes, "shape", "shapes") + (subs > 0 ? $" · {Plural(subs, "sub-point", "sub-points")}" : "");
    }

    /// <summary>What indenting a line does in this layout (the hint under the outline).</summary>
    public static string IndentHint(Layout? layout) => layout?.NestsAsShapes == true
        ? "Indent a line to put its box under the one above."
        : "Indent a line to make it a bullet point inside the shape above.";

    /// <summary>Why these rows don't suit the layout, or null when they do. Only an empty outline
    /// blocks Insert; the other hints are advice the dialog shows without disabling anything.</summary>
    public static string? Advice(Layout? layout, IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return null;
        var top = rows.Count(r => r.Level == 0);
        if (layout is null) return null;
        if (layout.NestsAsShapes && top > 1 && rows.Any(r => r.Level > 0))
            return $"{top} lines aren't indented, so the chart has {top} separate tops. Indent a line to put it under the one above.";
        if (layout.NestsAsShapes && rows.Count > 1 && rows.All(r => r.Level == 0))
            return "Every box is on one row. Indent the lines that report to the first one to build the chart.";
        if (layout.Family == SmartArtPreviewFamily.Matrix && top != 4)
            return $"A matrix has four quadrants; this outline has {top} top-level {(top == 1 ? "item" : "items")}.";
        if (layout.Family is SmartArtPreviewFamily.Venn && top > 4)
            return $"{top} circles overlap into a crowd. Venn diagrams read best with two to four.";
        if (top > 8)
            return $"{top} shapes is a lot for one diagram; the text will shrink to fit. Consider grouping some as indented sub-points.";
        return null;
    }

    /// <summary>The outline after an indent or outdent, and where the selection lands.</summary>
    public readonly record struct Edit(string Text, int SelectionStart, int SelectionLength);

    /// <summary>
    /// Indents (<paramref name="direction"/> &gt; 0) or outdents every line the selection
    /// [<paramref name="start"/>, <paramref name="end"/>) touches by two spaces (an outdent removes
    /// up to two spaces, or one tab). The selection moves with its text. Any of '\r\n', '\r' (WinUI's
    /// TextBox) or '\n' is a line break.
    /// </summary>
    public static Edit ShiftLines(string text, int start, int end, int direction)
    {
        text ??= "";
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(Math.Max(start, end), 0, text.Length);
        // A selection that ends right after a line break doesn't touch the next line.
        var last = end > start && end > 0 && text[end - 1] is '\r' or '\n' ? end - 1 : end;

        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            if (text[i] is '\r' or '\n') lineStarts.Add(i + 1);
        }

        var sb = new StringBuilder(text.Length + 16);
        int newStart = start, newEnd = end, copied = 0;
        foreach (var ls in lineStarts)
        {
            var nextBreak = ls;
            while (nextBreak < text.Length && text[nextBreak] is not ('\r' or '\n')) nextBreak++;
            var touched = ls <= last && nextBreak >= start;
            if (!touched) continue;
            sb.Append(text, copied, ls - copied);
            copied = ls;
            int delta;
            if (direction > 0)
            {
                sb.Append("  ");
                delta = 2;
            }
            else
            {
                var remove = 0;
                if (ls < text.Length && text[ls] == '\t') remove = 1;
                else while (remove < 2 && ls + remove < text.Length && text[ls + remove] == ' ') remove++;
                copied += remove;
                delta = -remove;
            }
            if (delta == 0) continue;
            // Positions after the line start shift with it; positions inside removed indent snap
            // to the line start.
            newStart = Shift(newStart, ls, start, delta);
            newEnd = Shift(newEnd, ls, end, delta);
        }
        sb.Append(text, copied, text.Length - copied);
        return new Edit(sb.ToString(), newStart, Math.Max(0, newEnd - newStart));

        static int Shift(int current, int lineStart, int original, int delta) =>
            original < lineStart ? current
            : delta > 0 ? current + delta
            : current - Math.Min(-delta, original - lineStart);
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
