using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Music;

public record MusicNote(string Pitch, int DurationNumerator, int DurationDenominator, bool IsBarLine);

public class AbcScoreModel
{
    public string Title { get; set; } = "Musical Score";
    public string Key { get; set; } = "C";
    public string Meter { get; set; } = "4/4";
    public List<MusicNote> Notes { get; } = new();
}

/// <summary>
/// Service for parsing ABC musical notation blocks and rendering clean 5-line musical staff SVG vector scores.
/// </summary>
public static class AbcMusicScoreRendererService
{
    private static readonly Regex MusicFenceRegex = new(
        @":::music(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses an ABC notation block into a score model.
    /// </summary>
    public static AbcScoreModel ParseAbc(string abcText, string defaultTitle = "Score")
    {
        var model = new AbcScoreModel { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(abcText))
            return model;

        var fence = MusicFenceRegex.Match(abcText);
        string body = fence.Success ? fence.Groups[2].Value : abcText;

        var lines = body.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var noteTokens = new List<string>();

        foreach (var line in lines)
        {
            string t = line.Trim();
            if (t.StartsWith("T:", StringComparison.OrdinalIgnoreCase)) model.Title = t.Substring(2).Trim();
            else if (t.StartsWith("M:", StringComparison.OrdinalIgnoreCase)) model.Meter = t.Substring(2).Trim();
            else if (t.StartsWith("K:", StringComparison.OrdinalIgnoreCase)) model.Key = t.Substring(2).Trim();
            else if (!t.StartsWith("X:", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("%"))
            {
                noteTokens.AddRange(t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        foreach (var tok in noteTokens)
        {
            if (tok == "|")
            {
                model.Notes.Add(new MusicNote("|", 0, 0, true));
            }
            else
            {
                model.Notes.Add(new MusicNote(tok, 1, 4, false));
            }
        }

        return model;
    }

    /// <summary>
    /// Renders an SVG musical staff score.
    /// </summary>
    public static string RenderStaffSvg(AbcScoreModel model)
    {
        double staffStartX = 40;
        double staffTopY = 50;
        double staffLineSpacing = 8;
        double staffWidth = Math.Max(300, model.Notes.Count * 28 + 80);
        double height = 120;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg width=\"{staffWidth}\" height=\"{height}\" viewBox=\"0 0 {staffWidth} {height}\" xmlns=\"http://www.w3.org/2000/svg\" class=\"ms-music-staff\">");
        sb.AppendLine("""
            <style>
              .m-title { font-family: Segoe UI, serif; font-size: 13px; font-weight: 700; fill: #e6edf3; }
              .staff-line { stroke: #8b949e; stroke-width: 1; }
              .note-head { fill: #58a6ff; }
              .note-stem { stroke: #58a6ff; stroke-width: 1.5; }
              .bar-line { stroke: #e6edf3; stroke-width: 1.5; }
              .clef-text { font-family: serif; font-size: 26px; fill: #e6edf3; }
            </style>
            """);

        sb.AppendLine($"  <text x=\"20\" y=\"24\" class=\"m-title\">{System.Net.WebUtility.HtmlEncode(model.Title)} (Key: {model.Key}, Meter: {model.Meter})</text>");

        // 1. Draw 5 Staff Lines
        for (int i = 0; i < 5; i++)
        {
            double y = staffTopY + i * staffLineSpacing;
            sb.AppendLine($"  <line x1=\"{staffStartX}\" y1=\"{y}\" x2=\"{staffStartX + staffWidth - 60}\" y2=\"{y}\" class=\"staff-line\" />");
        }

        // 2. Treble Clef Symbol & Meter
        sb.AppendLine($"  <text x=\"{staffStartX + 6}\" y=\"{staffTopY + 28}\" class=\"clef-text\">&#119070;</text>");

        // 3. Draw Notes and Bar Lines
        double curX = staffStartX + 50;
        foreach (var note in model.Notes)
        {
            if (note.IsBarLine)
            {
                sb.AppendLine($"  <line x1=\"{curX}\" y1=\"{staffTopY}\" x2=\"{curX}\" y2=\"{staffTopY + 32}\" class=\"bar-line\" />");
                curX += 20;
            }
            else
            {
                double noteY = GetNoteY(note.Pitch, staffTopY, staffLineSpacing);

                // Note head
                sb.AppendLine($"  <ellipse cx=\"{curX}\" cy=\"{noteY}\" rx=\"4.5\" ry=\"3.5\" transform=\"rotate(-20 {curX} {noteY})\" class=\"note-head\" />");
                // Stem
                sb.AppendLine($"  <line x1=\"{curX + 4}\" y1=\"{noteY}\" x2=\"{curX + 4}\" y2=\"{noteY - 22}\" class=\"note-stem\" />");

                curX += 26;
            }
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static double GetNoteY(string pitch, double staffTopY, double spacing)
    {
        // F5 is line 0 (top line = staffTopY)
        // E5 = +4, D5 = +8, C5 = +12, B4 = +16, A4 = +20, G4 = +24, F4 = +28, E4 = +32, D4 = +36, C4 = +40
        return pitch switch
        {
            "c" or "C5" => staffTopY + (spacing * 1.5),
            "B" or "B4" => staffTopY + (spacing * 2.0),
            "A" or "A4" => staffTopY + (spacing * 2.5),
            "G" or "G4" => staffTopY + (spacing * 3.0),
            "F" or "F4" => staffTopY + (spacing * 3.5),
            "E" or "E4" => staffTopY + (spacing * 4.0),
            "D" or "D4" => staffTopY + (spacing * 4.5),
            "C" or "C4" => staffTopY + (spacing * 5.0),
            _ => staffTopY + (spacing * 2.0)
        };
    }
}
