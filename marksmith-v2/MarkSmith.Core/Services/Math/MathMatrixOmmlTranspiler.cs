using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.MathTranspiler;

public record MatrixCell(string Text);
public record MatrixRow(List<MatrixCell> Cells);
public record MatrixModel(string Environment, List<MatrixRow> Rows, string BegChar, string EndChar);

/// <summary>
/// Service that parses LaTeX matrix and equation systems and transpiles them into native Office OpenXML OMML elements.
/// </summary>
public static class MathMatrixOmmlTranspiler
{
    private static readonly Regex MatrixRegex = new(
        @"\\begin\{(matrix|pmatrix|bmatrix|Bmatrix|vmatrix|Vmatrix|cases)\}([\s\S]*?)\\end\{\1\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses a LaTeX matrix block into a structured MatrixModel.
    /// </summary>
    public static MatrixModel? ParseMatrix(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex))
            return null;

        var match = MatrixRegex.Match(latex);
        if (!match.Success)
            return null;

        string env = match.Groups[1].Value;
        string body = match.Groups[2].Value.Trim();

        var (begChar, endChar) = env switch
        {
            "matrix" => ("", ""),
            "pmatrix" => ("(", ")"),
            "bmatrix" => ("[", "]"),
            "Bmatrix" => ("{", "}"),
            "vmatrix" => ("|", "|"),
            "Vmatrix" => ("‖", "‖"),
            "cases" => ("{", ""),
            _ => ("(", ")")
        };

        var rowStrings = Regex.Split(body, @"\\\\").Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        var rows = new List<MatrixRow>();

        foreach (var r in rowStrings)
        {
            var cellStrings = r.Split('&').Select(c => c.Trim()).ToList();
            var cells = cellStrings.Select(c => new MatrixCell(c)).ToList();
            rows.Add(new MatrixRow(cells));
        }

        return new MatrixModel(env, rows, begChar, endChar);
    }

    /// <summary>
    /// Transpiles a parsed MatrixModel into native Word OMML XML markup.
    /// </summary>
    public static string TranspileToOmml(MatrixModel matrix)
    {
        var sb = new StringBuilder();

        // 1. Delimiter wrapper <m:d>
        sb.Append($"<m:d xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"><m:dPr>");
        if (!string.IsNullOrEmpty(matrix.BegChar)) sb.Append($"<m:begChr m:val=\"{matrix.BegChar}\"/>");
        if (!string.IsNullOrEmpty(matrix.EndChar)) sb.Append($"<m:endChr m:val=\"{matrix.EndChar}\"/>");
        sb.Append("</m:dPr><m:e>");

        // 2. Matrix element <m:m>
        sb.Append("<m:m><m:mPr><m:baseJc m:val=\"center\"/><m:plcHide m:val=\"1\"/></m:mPr>");

        foreach (var row in matrix.Rows)
        {
            sb.Append("<m:mr>");
            foreach (var cell in row.Cells)
            {
                sb.Append("<m:e><m:r><m:t xml:space=\"preserve\">");
                sb.Append(System.Security.SecurityElement.Escape(cell.Text));
                sb.Append("</m:t></m:r></m:e>");
            }
            sb.Append("</m:mr>");
        }

        sb.Append("</m:m></m:e></m:d>");
        return sb.ToString();
    }
}
