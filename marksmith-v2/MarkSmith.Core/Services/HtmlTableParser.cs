using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

/// <summary>
/// Structured HTML table parser that transforms HTML tables (including colspan, rowspan,
/// nested tables, and rich inline formatting) into an AST and a normalized 2D grid
/// for OpenXML WordprocessingML and document rendering.
/// </summary>
public static class HtmlTableParser
{
    private static readonly Regex TableOpenRe = new(@"<table\b([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TheadRe = new(@"<thead\b[^>]*>([\s\S]*?)</thead\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TbodyRe = new(@"<tbody\b[^>]*>([\s\S]*?)</tbody\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TfootRe = new(@"<tfoot\b[^>]*>([\s\S]*?)</tfoot\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrRe = new(@"<tr\b([^>]*)>([\s\S]*?)</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CellTagRe = new(@"<(th|td)\b([^>]*)>([\s\S]*?)</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ColSpanAttrRe = new(@"colspan\s*=\s*[""']?(\d+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RowSpanAttrRe = new(@"rowspan\s*=\s*[""']?(\d+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AlignAttrRe = new(@"(?:align\s*=\s*[""']?(\w+)[""']?|text-align\s*:\s*(\w+))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses an HTML table string into an AST with normalized 2D grid matrix layout.
    /// </summary>
    public static HtmlTableNode? Parse(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var tableMatch = TableOpenRe.Match(html);
        string innerContent = html;
        if (tableMatch.Success)
        {
            int bodyStart = tableMatch.Index + tableMatch.Length;
            int closeAt = FindBalancedTagClose(html, "table", bodyStart);
            if (closeAt >= 0)
            {
                innerContent = html.Substring(bodyStart, closeAt - bodyStart);
            }
            else
            {
                innerContent = html[bodyStart..];
            }
        }

        var tableNode = new HtmlTableNode();

        // Extract rows from thead, tbody, tfoot or direct tr tags
        var rows = new List<(string TrAttrs, string InnerHtml, bool IsHeader)>();

        var theadMatch = TheadRe.Match(innerContent);
        if (theadMatch.Success)
        {
            rows.AddRange(ExtractTopLevelRows(theadMatch.Groups[1].Value, true));
        }

        var tbodyMatch = TbodyRe.Match(innerContent);
        if (tbodyMatch.Success)
        {
            rows.AddRange(ExtractTopLevelRows(tbodyMatch.Groups[1].Value, false));
        }

        var tfootMatch = TfootRe.Match(innerContent);
        if (tfootMatch.Success)
        {
            rows.AddRange(ExtractTopLevelRows(tfootMatch.Groups[1].Value, false));
        }

        // If no thead/tbody/tfoot containers matched, extract all tr directly
        if (rows.Count == 0)
        {
            rows.AddRange(ExtractTopLevelRows(innerContent, false));
        }

        if (rows.Count == 0) return null;

        foreach (var (trAttrs, trHtml, isHeaderSection) in rows)
        {
            var rowNode = new HtmlTableRowNode();
            var cellList = ExtractTopLevelCells(trHtml);
            bool allTh = cellList.Count > 0 && cellList.All(c => c.TagName.Equals("th", StringComparison.OrdinalIgnoreCase));
            rowNode.IsHeader = isHeaderSection || allTh;

            foreach (var (tagName, attrs, innerCellHtml) in cellList)
            {
                var cellNode = new HtmlTableCellNode
                {
                    IsHeader = tagName.Equals("th", StringComparison.OrdinalIgnoreCase) || rowNode.IsHeader
                };

                var csMatch = ColSpanAttrRe.Match(attrs);
                if (csMatch.Success && int.TryParse(csMatch.Groups[1].Value, out int cs) && cs > 0)
                {
                    cellNode.ColSpan = cs;
                }

                var rsMatch = RowSpanAttrRe.Match(attrs);
                if (rsMatch.Success && int.TryParse(rsMatch.Groups[1].Value, out int rs) && rs > 0)
                {
                    cellNode.RowSpan = rs;
                }

                var alignMatch = AlignAttrRe.Match(attrs);
                if (alignMatch.Success)
                {
                    cellNode.Align = alignMatch.Groups[1].Success ? alignMatch.Groups[1].Value : alignMatch.Groups[2].Value;
                }

                ParseCellContent(innerCellHtml, cellNode);
                rowNode.Cells.Add(cellNode);
            }

            if (rowNode.Cells.Count > 0)
            {
                tableNode.Rows.Add(rowNode);
            }
        }

        if (tableNode.Rows.Count == 0) return null;

        NormalizeGrid(tableNode);
        return tableNode;
    }

    /// <summary>
    /// Normalizes the table's 2D grid matrix to correctly handle colspan and rowspan spans,
    /// inserting continuation placeholder cells so OpenXML table structures match exact grid layouts.
    /// </summary>
    public static void NormalizeGrid(HtmlTableNode table)
    {
        int rowCount = table.Rows.Count;
        if (rowCount == 0) return;

        // Find max potential columns
        var rawRows = table.Rows.Select(r => r.Cells.ToList()).ToList();
        var grid = new Dictionary<(int r, int c), HtmlTableCellNode>();

        int maxCols = 0;
        for (int r = 0; r < rowCount; r++)
        {
            var rawCells = rawRows[r];
            int rawCellIdx = 0;
            int c = 0;

            while (rawCellIdx < rawCells.Count || grid.ContainsKey((r, c)))
            {
                if (grid.ContainsKey((r, c)))
                {
                    c++;
                    continue;
                }

                if (rawCellIdx < rawCells.Count)
                {
                    var cell = rawCells[rawCellIdx++];
                    int colSpan = Math.Max(1, cell.ColSpan);
                    int rowSpan = Math.Max(1, cell.RowSpan);

                    for (int dr = 0; dr < rowSpan; dr++)
                    {
                        for (int dc = 0; dc < colSpan; dc++)
                        {
                            int targetR = r + dr;
                            int targetC = c + dc;
                            if (dr == 0 && dc == 0)
                            {
                                grid[(targetR, targetC)] = cell;
                            }
                            else if (dc == 0)
                            {
                                // Vertical continuation cell
                                grid[(targetR, targetC)] = new HtmlTableCellNode
                                {
                                    IsHeader = cell.IsHeader,
                                    ColSpan = colSpan,
                                    RowSpan = 1,
                                    IsRowSpanContinuation = true
                                };
                            }
                            else
                            {
                                // Horizontal span placeholder (handled by ColSpan on the originating or continuation cell)
                                grid[(targetR, targetC)] = cell;
                            }
                        }
                    }

                    c += colSpan;
                    if (c > maxCols) maxCols = c;
                }
            }
        }

        // Reconstruct rows from the grid
        for (int r = 0; r < rowCount; r++)
        {
            table.Rows[r].Cells.Clear();
            int c = 0;
            while (c < maxCols)
            {
                if (grid.TryGetValue((r, c), out var cell))
                {
                    table.Rows[r].Cells.Add(cell);
                    c += Math.Max(1, cell.ColSpan);
                }
                else
                {
                    // Empty cell padding if row is short
                    table.Rows[r].Cells.Add(new HtmlTableCellNode { ColSpan = 1, RowSpan = 1 });
                    c++;
                }
            }
        }

        table.MaxColumns = maxCols;
    }

    /// <summary>
    /// Parses cell inner HTML into paragraphs, rich inline runs, links, code, and nested tables.
    /// </summary>
    private static void ParseCellContent(string innerHtml, HtmlTableCellNode cellNode)
    {
        if (string.IsNullOrWhiteSpace(innerHtml))
        {
            var p = new HtmlParagraphBlock();
            p.Inlines.Add(new HtmlTextInline { Text = "" });
            cellNode.Blocks.Add(p);
            return;
        }

        int pos = 0;
        var currentPara = new HtmlParagraphBlock();

        while (pos < innerHtml.Length)
        {
            // Look for nested table
            int tableIdx = innerHtml.IndexOf("<table", pos, StringComparison.OrdinalIgnoreCase);
            if (tableIdx >= 0)
            {
                // Parse preceding inline content
                if (tableIdx > pos)
                {
                    string preText = innerHtml.Substring(pos, tableIdx - pos);
                    ParseInlines(preText, currentPara);
                }

                if (currentPara.Inlines.Count > 0)
                {
                    cellNode.Blocks.Add(currentPara);
                    currentPara = new HtmlParagraphBlock();
                }

                int bodyStart = tableIdx + "<table".Length;
                int closeAt = FindBalancedTagClose(innerHtml, "table", bodyStart);
                if (closeAt >= 0)
                {
                    int tableEnd = closeAt + "</table>".Length;
                    string nestedTableHtml = innerHtml.Substring(tableIdx, tableEnd - tableIdx);
                    var nestedTable = Parse(nestedTableHtml);
                    if (nestedTable != null)
                    {
                        cellNode.Blocks.Add(new HtmlNestedTableBlock { Table = nestedTable });
                    }
                    pos = tableEnd;
                }
                else
                {
                    // Unclosed nested table, consume rest
                    string nestedTableHtml = innerHtml[tableIdx..];
                    var nestedTable = Parse(nestedTableHtml);
                    if (nestedTable != null)
                    {
                        cellNode.Blocks.Add(new HtmlNestedTableBlock { Table = nestedTable });
                    }
                    pos = innerHtml.Length;
                }
                continue;
            }

            // No nested table, parse remainder as inlines
            string remaining = innerHtml[pos..];
            ParseInlines(remaining, currentPara);
            pos = innerHtml.Length;
        }

        if (currentPara.Inlines.Count > 0 || cellNode.Blocks.Count == 0)
        {
            cellNode.Blocks.Add(currentPara);
        }
    }

    private static readonly Regex InlineTagRe = new(@"<(/?[a-zA-Z0-9]+)\b([^>]*)>", RegexOptions.Compiled);
    private static readonly Regex HrefAttrRe = new(@"href\s*=\s*[""']?([^""'\s>]+)[""']?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses inline markup (<b>, <strong>, <i>, <em>, <code>, <a>, <br>, <p>) into structured inlines.
    /// </summary>
    private static void ParseInlines(string html, HtmlParagraphBlock para)
    {
        if (string.IsNullOrEmpty(html)) return;

        bool bold = false;
        bool italic = false;
        bool code = false;
        string? currentHref = null;

        int cursor = 0;
        foreach (Match match in InlineTagRe.Matches(html))
        {
            if (match.Index > cursor)
            {
                string textChunk = html.Substring(cursor, match.Index - cursor);
                AddTextInline(textChunk, bold, italic, code, currentHref, para);
            }

            string tag = match.Groups[1].Value.ToLowerInvariant();
            string attrs = match.Groups[2].Value;

            switch (tag)
            {
                case "b" or "strong":
                    bold = true;
                    break;
                case "/b" or "/strong":
                    bold = false;
                    break;
                case "i" or "em":
                    italic = true;
                    break;
                case "/i" or "/em":
                    italic = false;
                    break;
                case "code":
                    code = true;
                    break;
                case "/code":
                    code = false;
                    break;
                case "a":
                    var hrefMatch = HrefAttrRe.Match(attrs);
                    currentHref = hrefMatch.Success ? hrefMatch.Groups[1].Value : "";
                    break;
                case "/a":
                    currentHref = null;
                    break;
                case "br" or "br/":
                    para.Inlines.Add(new HtmlBreakInline());
                    break;
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < html.Length)
        {
            string tail = html[cursor..];
            AddTextInline(tail, bold, italic, code, currentHref, para);
        }
    }

    private static void AddTextInline(string rawText, bool bold, bool italic, bool code, string? href, HtmlParagraphBlock para)
    {
        if (string.IsNullOrEmpty(rawText)) return;
        string decoded = WebUtility.HtmlDecode(rawText);
        if (string.IsNullOrEmpty(decoded)) return;

        if (!string.IsNullOrEmpty(href))
        {
            para.Inlines.Add(new HtmlLinkInline
            {
                Href = href,
                Text = decoded,
                Bold = bold,
                Italic = italic
            });
        }
        else
        {
            para.Inlines.Add(new HtmlTextInline
            {
                Text = decoded,
                Bold = bold,
                Italic = italic,
                Code = code
            });
        }
    }

    private static int FindBalancedTagClose(string raw, string tagName, int bodyStart)
    {
        int depth = 1, i = bodyStart;
        string openTag = "<" + tagName;
        string closeTag = "</" + tagName + ">";

        while (i < raw.Length)
        {
            int open = raw.IndexOf(openTag, i, StringComparison.OrdinalIgnoreCase);
            int close = raw.IndexOf(closeTag, i, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return -1;
            if (open >= 0 && open < close)
            {
                depth++;
                i = open + openTag.Length;
            }
            else
            {
                depth--;
                if (depth == 0) return close;
                i = close + closeTag.Length;
            }
        }
        return -1;
    }

    private static List<(string TrAttrs, string InnerHtml, bool IsHeader)> ExtractTopLevelRows(string containerHtml, bool isHeaderSection)
    {
        var rows = new List<(string TrAttrs, string InnerHtml, bool IsHeader)>();
        int pos = 0;
        while (pos < containerHtml.Length)
        {
            int trOpen = containerHtml.IndexOf("<tr", pos, StringComparison.OrdinalIgnoreCase);
            if (trOpen < 0) break;

            int tableOpen = containerHtml.IndexOf("<table", pos, StringComparison.OrdinalIgnoreCase);
            if (tableOpen >= 0 && tableOpen < trOpen)
            {
                int closeTable = FindBalancedTagClose(containerHtml, "table", tableOpen + "<table".Length);
                if (closeTable >= 0)
                {
                    pos = closeTable + "</table>".Length;
                    continue;
                }
            }

            int tagEnd = containerHtml.IndexOf('>', trOpen);
            if (tagEnd < 0) break;

            string trTag = containerHtml.Substring(trOpen, tagEnd - trOpen + 1);
            var trMatch = Regex.Match(trTag, @"^<tr\b([^>]*)>", RegexOptions.IgnoreCase);
            string trAttrs = trMatch.Success ? trMatch.Groups[1].Value : "";

            int bodyStart = tagEnd + 1;
            int trClose = FindTopLevelTrClose(containerHtml, bodyStart);
            if (trClose >= 0)
            {
                string innerTr = containerHtml.Substring(bodyStart, trClose - bodyStart);
                rows.Add((trAttrs, innerTr, isHeaderSection));
                pos = trClose + "</tr>".Length;
            }
            else
            {
                string innerTr = containerHtml[bodyStart..];
                rows.Add((trAttrs, innerTr, isHeaderSection));
                break;
            }
        }
        return rows;
    }

    private static int FindTopLevelTrClose(string raw, int bodyStart)
    {
        int i = bodyStart;
        while (i < raw.Length)
        {
            int nextTrClose = raw.IndexOf("</tr>", i, StringComparison.OrdinalIgnoreCase);
            if (nextTrClose < 0) return -1;

            int nextTableOpen = raw.IndexOf("<table", i, StringComparison.OrdinalIgnoreCase);
            if (nextTableOpen >= 0 && nextTableOpen < nextTrClose)
            {
                int tableClose = FindBalancedTagClose(raw, "table", nextTableOpen + "<table".Length);
                if (tableClose >= 0)
                {
                    i = tableClose + "</table>".Length;
                    continue;
                }
            }

            return nextTrClose;
        }
        return -1;
    }

    private static List<(string TagName, string Attrs, string InnerHtml)> ExtractTopLevelCells(string trHtml)
    {
        var cells = new List<(string TagName, string Attrs, string InnerHtml)>();
        int pos = 0;
        while (pos < trHtml.Length)
        {
            int thIdx = trHtml.IndexOf("<th", pos, StringComparison.OrdinalIgnoreCase);
            int tdIdx = trHtml.IndexOf("<td", pos, StringComparison.OrdinalIgnoreCase);

            int cellOpen;
            string tagName;
            if (thIdx >= 0 && (tdIdx < 0 || thIdx < tdIdx))
            {
                cellOpen = thIdx;
                tagName = "th";
            }
            else if (tdIdx >= 0)
            {
                cellOpen = tdIdx;
                tagName = "td";
            }
            else
            {
                break;
            }

            int tagEnd = trHtml.IndexOf('>', cellOpen);
            if (tagEnd < 0) break;

            string openTagStr = trHtml.Substring(cellOpen, tagEnd - cellOpen + 1);
            var cellMatch = Regex.Match(openTagStr, @"^<(th|td)\b([^>]*)>", RegexOptions.IgnoreCase);
            string attrs = cellMatch.Success ? cellMatch.Groups[2].Value : "";

            int bodyStart = tagEnd + 1;
            int cellClose = FindTopLevelCellClose(trHtml, tagName, bodyStart);
            if (cellClose >= 0)
            {
                string inner = trHtml.Substring(bodyStart, cellClose - bodyStart);
                cells.Add((tagName, attrs, inner));
                pos = cellClose + ("</" + tagName + ">").Length;
            }
            else
            {
                string inner = trHtml[bodyStart..];
                cells.Add((tagName, attrs, inner));
                break;
            }
        }
        return cells;
    }

    private static int FindTopLevelCellClose(string raw, string tagName, int bodyStart)
    {
        int i = bodyStart;
        string closeTag = "</" + tagName + ">";
        while (i < raw.Length)
        {
            int nextClose = raw.IndexOf(closeTag, i, StringComparison.OrdinalIgnoreCase);
            if (nextClose < 0) return -1;

            int nextTableOpen = raw.IndexOf("<table", i, StringComparison.OrdinalIgnoreCase);
            if (nextTableOpen >= 0 && nextTableOpen < nextClose)
            {
                int tableClose = FindBalancedTagClose(raw, "table", nextTableOpen + "<table".Length);
                if (tableClose >= 0)
                {
                    i = tableClose + "</table>".Length;
                    continue;
                }
            }

            return nextClose;
        }
        return -1;
    }
}

public class HtmlTableNode
{
    public List<HtmlTableRowNode> Rows { get; } = new();
    public int MaxColumns { get; set; }
}

public class HtmlTableRowNode
{
    public bool IsHeader { get; set; }
    public List<HtmlTableCellNode> Cells { get; } = new();
}

public class HtmlTableCellNode
{
    public bool IsHeader { get; set; }
    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public bool IsRowSpanContinuation { get; set; }
    public string? Align { get; set; }
    public List<HtmlCellContentBlock> Blocks { get; } = new();
}

public abstract class HtmlCellContentBlock { }

public class HtmlParagraphBlock : HtmlCellContentBlock
{
    public List<HtmlInlineNode> Inlines { get; } = new();
}

public class HtmlNestedTableBlock : HtmlCellContentBlock
{
    public HtmlTableNode Table { get; set; } = new();
}

public abstract class HtmlInlineNode { }

public class HtmlTextInline : HtmlInlineNode
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Code { get; set; }
}

public class HtmlLinkInline : HtmlInlineNode
{
    public string Href { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
}

public class HtmlBreakInline : HtmlInlineNode { }
