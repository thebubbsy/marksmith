using System.Linq;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkSmith.Services;

/// <summary>
/// A pipe table whose header row is blank (<c>|  |  |</c> above the delimiter row). Markdown has no
/// header-less table, so the Table dialog writes a blank header row when "Include header row" is
/// cleared (run #329). The preview hides that row, and Word, PowerPoint and email leave it out, so
/// the table comes out header-less as asked instead of opening on an empty shaded row.
/// </summary>
public static class BlankTableHeader
{
    /// <summary>True for a header row with nothing in any cell, in a table that has body rows too
    /// (a table that is only a blank header keeps it, so it isn't left with no rows at all).</summary>
    public static bool IsBlank(TableRow row)
    {
        if (!row.IsHeader || row.Parent is not Table table) return false;
        if (!table.OfType<TableRow>().Any(r => !r.IsHeader)) return false;
        return row.OfType<TableCell>().All(IsEmpty);
    }

    private static bool IsEmpty(TableCell cell) =>
        !cell.Descendants<LinkInline>().Any()
        && cell.Descendants<LeafInline>().All(l => l is LiteralInline lit && lit.Content.IsEmptyOrWhitespace());
}
