using System;
using System.Globalization;

namespace MarkSmith.Services;

/// <summary>
/// What the "Recover unsaved document" prompt shows about the draft it found: the document's
/// first line as a title and when it was last saved with its length, so Restore / Discard is a
/// decision about a recognisable piece of work rather than a blind guess.
/// </summary>
public static class DraftSummary
{
    private const int TitleMax = 70;

    public static (string Title, string Detail) Describe(string markdown, DateTime savedAt, DateTime now)
    {
        var words = DocumentStatsService.Analyze(markdown).Words;
        var detail = $"Last saved {When(savedAt, now)} · {words:N0} {(words == 1 ? "word" : "words")}";
        return (Title(markdown), detail);
    }

    // The first line with text in it, without its Markdown markers (# heading, > quote, - bullet,
    // **bold**), cut at a word boundary.
    internal static string Title(string markdown)
    {
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("```", StringComparison.Ordinal) || line == "---") continue;
            line = line.TrimStart('#', '>', ' ', '\t');
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = line[2..];
            line = line.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
            if (line.Length == 0) continue;
            if (line.Length <= TitleMax) return line;
            var cut = line.LastIndexOf(' ', TitleMax);
            return line[..(cut > TitleMax / 2 ? cut : TitleMax)].TrimEnd(' ', ',', ';', ':', '.') + "…";
        }
        return "Untitled draft";
    }

    internal static string When(DateTime savedAt, DateTime now)
    {
        var time = savedAt.ToString("t", CultureInfo.CurrentCulture);
        var age = now - savedAt;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (savedAt.Date == now.Date) return $"today at {time}";
        if (savedAt.Date == now.Date.AddDays(-1)) return $"yesterday at {time}";
        return $"{savedAt.ToString("d MMM", CultureInfo.CurrentCulture)} at {time}";
    }
}
