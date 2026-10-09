using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Editor;

/// <summary>
/// Folding for the editor, as a VIEW of the document. The document itself never changes: the
/// editor shows a "visible" text in which each folded region is its first line plus a short
/// marker (<c>## Details  «+5 lines folded #2»</c>), and the hidden lines live here, keyed by the
/// marker's number. <see cref="Document"/> turns any visible text back into the full document, so
/// the preview, exports, saving, word count and undo all see every line.
///
/// It replaces <see cref="EditorFoldingService"/>'s approach, which wrote the hidden lines into the
/// document as a base64 HTML comment: folded sections vanished from the preview and every export,
/// and a save wrote the comment to disk. <see cref="RepairLegacy"/> expands those old comments.
///
/// Line separators are kept as found (the WinUI TextBox uses a bare '\r'), and lines are 1-based.
/// One instance per editor; the hidden lines only need to live as long as the window.
/// </summary>
public sealed class EditorFolds
{
    private readonly Dictionary<int, string[]> _hidden = new();
    private int _nextId = 1;

    private static readonly Regex MarkerRe = new(@"  «\+(\d+) lines? folded #(\d+)»$", RegexOptions.Compiled);
    private static readonly Regex AnyMarkerRe = new(@"  «\+\d+ lines? folded #\d+»", RegexOptions.Compiled);
    private static readonly Regex HeadingRe = new(@"^ {0,3}(#{1,6})(?:[ \t]|$)", RegexOptions.Compiled);
    private static readonly Regex FenceRe = new(@"^ {0,3}(`{3,}|~{3,})(.*)$", RegexOptions.Compiled);
    private static readonly Regex BlockOpenRe = new(@"^ {0,3}:::\s*\S", RegexOptions.Compiled);
    private static readonly Regex BlockCloseRe = new(@"^ {0,3}:::\s*$", RegexOptions.Compiled);

    /// <summary>The marker text appended to a folded region's first line.</summary>
    public static string Marker(int hiddenLines, int id) =>
        $"  «+{hiddenLines} {(hiddenLines == 1 ? "line" : "lines")} folded #{id}»";

    /// <summary>Does this visible text contain any fold this instance can open?</summary>
    public bool HasFolds(string visible) => Count(visible) > 0;

    /// <summary>Number of folds in the visible text (outermost only; nested ones are inside them).</summary>
    public int Count(string visible)
    {
        if (string.IsNullOrEmpty(visible) || visible.IndexOf('«') < 0) return 0;
        var n = 0;
        foreach (var line in SplitLines(visible, out _))
            if (TryMarker(line, out _, out _)) n++;
        return n;
    }

    /// <summary>The full document behind a visible text: every fold opened, recursively.</summary>
    public string Document(string visible)
    {
        if (string.IsNullOrEmpty(visible) || visible.IndexOf('«') < 0) return visible ?? string.Empty;
        var lines = SplitLines(visible, out var nl);
        var output = new List<string>(lines.Length + 16);
        foreach (var line in lines) Expand(line, output, recursive: true);
        return string.Join(nl, output);
    }

    /// <summary>Opens every fold (the same text as <see cref="Document"/>).</summary>
    public string UnfoldAll(string visible) => Document(visible);

    /// <summary>
    /// Folds or unfolds at a visible line: a folded line opens one level; otherwise the region that
    /// starts on the line folds (a heading's section, a code block, a ::: block), or failing that the
    /// smallest region containing it. Returns the text unchanged when there is nothing to fold.
    /// </summary>
    public string Toggle(string visible, int line) => Toggle(visible, line, out _);

    /// <param name="foldedLine">The visible line the caret should go to (the folded or opened line).</param>
    public string Toggle(string visible, int line, out int foldedLine)
    {
        foldedLine = line;
        if (string.IsNullOrEmpty(visible)) return visible ?? string.Empty;
        var lines = SplitLines(visible, out var nl);
        if (line < 1 || line > lines.Length) return visible;

        if (TryMarker(lines[line - 1], out _, out _))
        {
            var output = new List<string>(lines.Length + 16);
            for (var i = 0; i < lines.Length; i++)
            {
                if (i == line - 1) Expand(lines[i], output, recursive: false);
                else output.Add(lines[i]);
            }
            return string.Join(nl, output);
        }

        var regions = Regions(lines);
        var region = regions.Where(r => r.Start == line).OrderBy(r => r.End - r.Start).FirstOrDefault()
                  ?? regions.Where(r => r.Start < line && line <= r.End).OrderBy(r => r.End - r.Start).FirstOrDefault();
        if (region is null) return visible;
        foldedLine = region.Start;
        return string.Join(nl, Fold(lines, region));
    }

    /// <summary>Folds every code block and ::: block (outermost ones), leaving headings open.</summary>
    public string FoldAllCode(string visible)
    {
        if (string.IsNullOrEmpty(visible)) return visible ?? string.Empty;
        var lines = SplitLines(visible, out var nl);
        var blocks = Regions(lines).Where(r => r.Kind != FoldKind.Section).OrderBy(r => r.Start).ToList();
        var outer = new List<FoldRegion>();
        foreach (var r in blocks)
            if (!outer.Any(o => o.Start <= r.Start && r.End <= o.End)) outer.Add(r);
        if (outer.Count == 0) return visible;

        IList<string> current = lines;
        foreach (var r in outer.OrderByDescending(r => r.Start)) current = Fold(current, r);
        return string.Join(nl, current);
    }

    /// <summary>
    /// Maps a visible line to the document line it shows (counts the lines hidden above it).
    /// </summary>
    public int DocumentLine(string visible, int visibleLine)
    {
        if (string.IsNullOrEmpty(visible) || visible.IndexOf('«') < 0) return visibleLine;
        var lines = SplitLines(visible, out _);
        var docLine = visibleLine;
        for (var i = 0; i < Math.Min(visibleLine - 1, lines.Length); i++)
            docLine += HiddenCount(lines[i]);
        return docLine;
    }

    /// <summary>
    /// The document line number of every visible line, in order (the gutter's numbers: after a
    /// fold they jump by the lines it hides). <paramref name="lineCount"/> is the visible line count.
    /// </summary>
    public int[] DocumentLineNumbers(string visible, int lineCount)
    {
        var numbers = new int[Math.Max(0, lineCount)];
        if (numbers.Length == 0) return numbers;
        string[]? lines = string.IsNullOrEmpty(visible) || visible.IndexOf('«') < 0 ? null : SplitLines(visible, out _);
        var doc = 1;
        for (var i = 0; i < numbers.Length; i++)
        {
            numbers[i] = doc;
            doc += 1 + (lines is not null && i < lines.Length ? HiddenCount(lines[i]) : 0);
        }
        return numbers;
    }

    /// <summary>
    /// The visible line showing a document line, or 0 when a fold hides it (open the folds first).
    /// </summary>
    public int VisibleLine(string visible, int documentLine)
    {
        if (documentLine < 1) return 0;
        if (string.IsNullOrEmpty(visible) || visible.IndexOf('«') < 0) return documentLine;
        var lines = SplitLines(visible, out _);
        var doc = 1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (doc == documentLine) return i + 1;
            var hidden = HiddenCount(lines[i]);
            if (documentLine > doc && documentLine <= doc + hidden) return 0;
            doc += 1 + hidden;
        }
        return 0;
    }

    /// <summary>Is this visible line a fold's first line?</summary>
    public bool IsFoldedLine(string line) => TryMarker(line, out _, out _);

    /// <summary>Foldable regions of a visible text, for tests and the UI's "can fold here" checks.</summary>
    public static IReadOnlyList<FoldRegion> Regions(string visible) =>
        string.IsNullOrEmpty(visible) ? Array.Empty<FoldRegion>() : Regions(SplitLines(visible, out _));

    /// <summary>
    /// Expands folds that older versions wrote INTO the document (<c>&lt;!-- FOLDED:… --&gt;</c>
    /// comments). Returns the text unchanged when there are none; <paramref name="repaired"/> says
    /// how many were opened.
    /// </summary>
    public static string RepairLegacy(string text, out int repaired)
    {
        repaired = 0;
        if (string.IsNullOrEmpty(text) || !text.Contains("<!-- FOLDED:", StringComparison.Ordinal)) return text ?? string.Empty;
        var count = EditorFoldingService.GetFoldedCount(text);
        if (count == 0) return text;
        var lines = SplitLines(text, out var nl);
        var output = new List<string>(lines.Length + 32);
        foreach (var line in lines)
        {
            // The old placeholder was "<header> /* ▾ [N lines folded] */ <!-- FOLDED:… -->" and the
            // payload included the header line, so the whole placeholder line is replaced.
            if (line.Contains("<!-- FOLDED:", StringComparison.Ordinal))
            {
                string restored;
                try { restored = EditorFoldingService.UnfoldAll(line); }
                catch (FormatException) { output.Add(line); continue; }
                output.AddRange(SplitLines(restored, out _));
            }
            else output.Add(line);
        }
        repaired = count;
        return string.Join(nl, output);
    }

    // ---- internals ----

    private IList<string> Fold(IList<string> lines, FoldRegion region)
    {
        var id = _nextId++;
        var hidden = lines.Skip(region.Start).Take(region.End - region.Start).ToArray();
        _hidden[id] = hidden;
        var result = new List<string>(lines.Count - hidden.Length);
        for (var i = 0; i < lines.Count; i++)
        {
            var n = i + 1;
            if (n == region.Start) result.Add(lines[i] + Marker(CountDocumentLines(hidden), id));
            else if (n <= region.Start || n > region.End) result.Add(lines[i]);
        }
        return result;
    }

    // Lines the hidden block stands for in the document (folds inside it count in full).
    private int CountDocumentLines(IEnumerable<string> hidden)
    {
        var n = 0;
        foreach (var l in hidden) n += 1 + HiddenCount(l);
        return n;
    }

    private int HiddenCount(string line)
    {
        if (!TryMarker(line, out _, out var id) || !_hidden.TryGetValue(id, out var hidden)) return 0;
        return CountDocumentLines(hidden);
    }

    private void Expand(string line, List<string> output, bool recursive)
    {
        if (!TryMarker(line, out var head, out var id) || !_hidden.TryGetValue(id, out var hidden))
        {
            output.Add(line);
            return;
        }
        output.Add(head);
        foreach (var h in hidden)
        {
            if (recursive) Expand(h, output, recursive: true);
            else output.Add(h);
        }
    }

    private bool TryMarker(string line, out string head, out int id)
    {
        head = line;
        id = 0;
        if (line.Length == 0 || line[^1] != '»') return false;
        var m = MarkerRe.Match(line);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out id) || !_hidden.ContainsKey(id)) return false;
        head = line[..m.Index];
        return true;
    }

    private static IReadOnlyList<FoldRegion> Regions(IList<string> lines)
    {
        var regions = new List<FoldRegion>();
        var headings = new List<(int Line, int Level)>();
        var blockStack = new Stack<int>();
        string? fence = null;
        var fenceStart = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var n = i + 1;
            // A folded line is closed: its fence or ::: block is inside the fold.
            var line = AnyMarkerRe.Replace(lines[i], "");
            var folded = line.Length != lines[i].Length;

            if (fence is not null)
            {
                var close = FenceRe.Match(line);
                if (close.Success && close.Groups[2].Value.Trim().Length == 0
                    && close.Groups[1].Value[0] == fence[0] && close.Groups[1].Value.Length >= fence.Length)
                {
                    if (n > fenceStart) regions.Add(new FoldRegion(fenceStart, n, FoldKind.CodeBlock, lines[fenceStart - 1]));
                    fence = null;
                }
                continue;
            }

            var h = HeadingRe.Match(line);
            if (h.Success) { headings.Add((n, h.Groups[1].Value.Length)); continue; }
            if (folded) continue;

            var open = FenceRe.Match(line);
            if (open.Success && !(open.Groups[1].Value[0] == '`' && open.Groups[2].Value.Contains('`')))
            {
                fence = open.Groups[1].Value;
                fenceStart = n;
                continue;
            }
            if (BlockCloseRe.IsMatch(line))
            {
                if (blockStack.Count > 0)
                {
                    var start = blockStack.Pop();
                    regions.Add(new FoldRegion(start, n, FoldKind.Block, lines[start - 1]));
                }
                continue;
            }
            if (BlockOpenRe.IsMatch(line)) blockStack.Push(n);
        }

        // A heading's section runs to the line before the next heading of the same or a higher
        // level, without the blank lines at its end (they stay visible as the gap before it).
        for (var k = 0; k < headings.Count; k++)
        {
            var (start, level) = headings[k];
            var end = lines.Count;
            for (var j = k + 1; j < headings.Count; j++)
                if (headings[j].Level <= level) { end = headings[j].Line - 1; break; }
            while (end > start && lines[end - 1].Trim().Length == 0) end--;
            if (end > start) regions.Add(new FoldRegion(start, end, FoldKind.Section, lines[start - 1]));
        }
        return regions.OrderBy(r => r.Start).ToList();
    }

    private static string[] SplitLines(string text, out string newline)
    {
        newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : text.IndexOf('\r') >= 0 ? "\r"
            : "\n";
        return text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
    }
}

public enum FoldKind { Section, CodeBlock, Block }

/// <summary>A foldable range of lines (1-based, inclusive). The first line stays visible.</summary>
public sealed record FoldRegion(int Start, int End, FoldKind Kind, string Header);
