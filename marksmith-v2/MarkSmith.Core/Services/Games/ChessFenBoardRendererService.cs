using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Games;

public class ChessBoardModel
{
    public string Title { get; set; } = "Chess Position";
    public string Fen { get; set; } = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    public char[,] Board { get; } = new char[8, 8];
    public string? FocusSquare { get; set; }
}

/// <summary>
/// Service for parsing Forsyth-Edwards Notation (FEN) chess positions and rendering 8x8 SVG chessboards.
/// </summary>
public static class ChessFenBoardRendererService
{
    private static readonly Regex ChessFenceRegex = new(
        @":::chess(?:\s+""([^""]+)"")?(?:\s+([^\r\n]+))?\r?\n?([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex FenRegex = new(
        @"FEN:\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FocusRegex = new(
        @"focus:\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<char, string> PieceGlyphs = new()
    {
        { 'K', "♔" }, { 'Q', "♕" }, { 'R', "♖" }, { 'B', "♗" }, { 'N', "♘" }, { 'P', "♙" },
        { 'k', "♚" }, { 'q', "♛" }, { 'r', "♜" }, { 'b', "♝" }, { 'n', "♞" }, { 'p', "♟" }
    };

    public static ChessBoardModel ParseChess(string blockText, string defaultTitle = "Chess Position")
    {
        var model = new ChessBoardModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(blockText))
        {
            PopulateFromFen(model, model.Fen);
            return model;
        }

        var fence = ChessFenceRegex.Match(blockText);
        string text = blockText;
        if (fence.Success)
        {
            string header = fence.Groups[1].Success ? fence.Groups[1].Value : "";
            if (!string.IsNullOrWhiteSpace(header)) model.Title = header.Trim();
            text = (fence.Groups[2].Value + " " + fence.Groups[3].Value);
        }

        var fm = FenRegex.Match(text);
        if (fm.Success) model.Fen = fm.Groups[1].Value.Trim();

        var focm = FocusRegex.Match(text);
        if (focm.Success) model.FocusSquare = focm.Groups[1].Value.ToLowerInvariant().Trim();

        PopulateFromFen(model, model.Fen);
        return model;
    }

    private static void PopulateFromFen(ChessBoardModel model, string fen)
    {
        string piecePlacement = fen.Split(' ')[0];
        var ranks = piecePlacement.Split('/');
        for (int r = 0; r < Math.Min(8, ranks.Length); r++)
        {
            string rankStr = ranks[r];
            int file = 0;
            foreach (char c in rankStr)
            {
                if (file >= 8) break;
                if (char.IsDigit(c))
                {
                    int emptyCount = c - '0';
                    for (int k = 0; k < emptyCount && file < 8; k++)
                    {
                        model.Board[r, file++] = '.';
                    }
                }
                else
                {
                    model.Board[r, file++] = c;
                }
            }
        }
    }

    public static string RenderChessSvg(ChessBoardModel model)
    {
        double tileSize = 32;
        double boardW = tileSize * 8;
        double width = boardW + 70;
        double height = boardW + 90;
        double ox = 35;
        double oy = 55;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-chess-svg\">");
        sb.AppendLine("""
            <style>
              .ch-bg { fill: #0f172a; stroke: #1e293b; stroke-width: 1.5; }
              .ch-title { font-family: Segoe UI, sans-serif; font-size: 13px; font-weight: 700; fill: #f8fafc; }
              .ch-light { fill: #f1f5f9; }
              .ch-dark { fill: #64748b; }
              .ch-focus { stroke: #eab308; stroke-width: 2.5; }
              .ch-coord { font-family: monospace; font-size: 9px; fill: #94a3b8; text-anchor: middle; }
              .ch-piece { font-family: Segoe UI Symbol, sans-serif; font-size: 22px; text-anchor: middle; }
              .piece-white { fill: #ffffff; stroke: #0f172a; stroke-width: 0.5; }
              .piece-black { fill: #0f172a; }
            </style>
            """);

        sb.AppendLine($"  <rect width=\"{width}\" height=\"{height}\" rx=\"8\" class=\"ch-bg\" />");
        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"ch-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</text>");

        // Files (a-h)
        for (int f = 0; f < 8; f++)
        {
            char fileChar = (char)('a' + f);
            double fx = ox + f * tileSize + tileSize / 2;
            sb.AppendLine($"  <text x=\"{fx}\" y=\"{oy + boardW + 16}\" class=\"ch-coord\">{fileChar}</text>");
        }

        // Ranks (8-1)
        for (int r = 0; r < 8; r++)
        {
            int rankNum = 8 - r;
            double ry = oy + r * tileSize + tileSize / 2 + 3;
            sb.AppendLine($"  <text x=\"{ox - 12}\" y=\"{ry}\" class=\"ch-coord\">{rankNum}</text>");
        }

        // Squares & Pieces
        for (int r = 0; r < 8; r++)
        {
            for (int f = 0; f < 8; f++)
            {
                double sx = ox + f * tileSize;
                double sy = oy + r * tileSize;
                bool isLight = (r + f) % 2 == 0;
                string sqClass = isLight ? "ch-light" : "ch-dark";

                string sqName = $"{(char)('a' + f)}{8 - r}";
                bool isFocus = string.Equals(sqName, model.FocusSquare, StringComparison.OrdinalIgnoreCase);
                string focusClass = isFocus ? "ch-focus" : "";

                sb.AppendLine($"  <rect x=\"{sx}\" y=\"{sy}\" width=\"{tileSize}\" height=\"{tileSize}\" class=\"{sqClass} {focusClass}\" />");

                char p = model.Board[r, f];
                if (p != '.' && p != '\0' && PieceGlyphs.TryGetValue(p, out string? glyph))
                {
                    string pClass = char.IsUpper(p) ? "piece-white" : "piece-black";
                    sb.AppendLine($"  <text x=\"{sx + tileSize / 2}\" y=\"{sy + tileSize / 2 + 7}\" class=\"ch-piece {pClass}\">{glyph}</text>");
                }
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
