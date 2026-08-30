using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Splits one row of a <c>:::parallel</c> block into its columns.
///
/// The block supports two column separators, and shipped documents use both:
/// <list type="bullet">
///   <item>a <c>===</c> line between columns, and</item>
///   <item>a leading <c>|</c> per line — one pipe opens each column, and any following unprefixed
///         lines continue it, so a column can still span multiple lines.</item>
/// </list>
/// Only the <c>===</c> form was ever implemented, so the <c>|</c> form — the one the bundled
/// bilingual-contract example is written in — collapsed every language into column one and left the
/// rest of the row blank, in both the DOCX and preview pipelines.
/// </summary>
public static partial class ParallelRowParser
{
    [GeneratedRegex(@"(?:\r?\n|^)===(?:\r?\n|$)")]
    private static partial Regex ColumnSeparatorRe();

    [GeneratedRegex(@"^\s*\|\s?")]
    private static partial Regex PipePrefixRe();

    /// <summary>
    /// Returns the row's columns, padded with empty strings out to <paramref name="columnCount"/>.
    /// </summary>
    public static string[] SplitColumns(string row, int columnCount)
    {
        var columns = Split(row ?? "");
        var result = new string[columnCount];
        for (int i = 0; i < columnCount; i++)
            result[i] = i < columns.Count ? columns[i].Trim() : "";
        return result;
    }

    private static List<string> Split(string row)
    {
        // An explicit === separator always wins; it is unambiguous.
        if (ColumnSeparatorRe().IsMatch(row))
            return ColumnSeparatorRe().Split(row).ToList();

        var lines = row.Split('\n');
        if (!lines.Any(l => PipePrefixRe().IsMatch(l)))
            return new List<string> { row };

        var columns = new List<string>();
        StringBuilder? current = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var pipe = PipePrefixRe().Match(line);
            if (pipe.Success)
            {
                if (current is not null) columns.Add(current.ToString().TrimEnd());
                current = new StringBuilder(line[pipe.Length..]);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            // A line without a pipe continues the column it follows, so a cell can hold a
            // paragraph. Text before the first pipe has no column yet, so it opens one.
            current ??= new StringBuilder();
            if (current.Length > 0) current.Append('\n');
            current.Append(line);
        }

        if (current is not null) columns.Add(current.ToString().TrimEnd());
        return columns;
    }
}
