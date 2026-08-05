using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MarkSmith.Services;

/// <summary>
/// Builds the Markdown snippets that the Insert-menu modals emit from the parameters the user
/// collected in a dialog. Pure functions with no UI dependency so the generation logic is
/// unit-testable independent of WinUI. The ProMode paths bypass these entirely and insert the
/// classic raw placeholders directly (see the On*Click handlers in MainWindow.xaml.cs).
/// </summary>
public static class InsertSnippetBuilder
{
    /// <summary>Pipe table. <paramref name="rows"/> counts body rows (the optional header row and
    /// the mandatory separator row come on top). Defaults reproduce the legacy placeholder.</summary>
    public static string Table(int rows, int cols, bool includeHeaderRow)
    {
        rows = Math.Clamp(rows, 1, 50);
        cols = Math.Clamp(cols, 1, 20);
        var sb = new StringBuilder("\n");
        if (includeHeaderRow)
        {
            sb.Append('|');
            for (var c = 1; c <= cols; c++) sb.Append($" Header {c} |");
            sb.Append('\n');
        }
        sb.Append('|'); // delimiter row — required by Markdown tables with or without a header
        for (var c = 0; c < cols; c++) sb.Append(" --- |");
        sb.Append('\n');
        var cell = 1;
        for (var r = 0; r < rows; r++)
        {
            sb.Append('|');
            for (var c = 0; c < cols; c++) sb.Append($" Value {cell++} |");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>[text](url). An empty URL falls back to the literal "url" placeholder.</summary>
    public static string Link(string text, string url) =>
        $"[{(text ?? "").Trim()}]({Or(url, "url")})";

    /// <summary>Fenced code block; an empty language yields a bare ``` fence.</summary>
    public static string CodeBlock(string language, string body) =>
        $"\n```{(language ?? "").Trim()}\n{(body ?? "").TrimEnd()}\n```\n";

    /// <summary>:::chart block from "label,value" lines.</summary>
    public static string Chart(string type, IEnumerable<string> labelValueLines)
    {
        var sb = new StringBuilder($"\n:::chart type=\"{Or(type, "bar")}\"\n");
        foreach (var line in Clean(labelValueLines)) sb.Append(line).Append('\n');
        return sb.Append(":::\n").ToString();
    }

    /// <summary>:::columns block with <paramref name="count"/> (2-4) ===-separated placeholders.</summary>
    public static string Columns(int count)
    {
        count = Math.Clamp(count, 2, 4);
        var sb = new StringBuilder($"\n:::columns count=\"{count}\"\n");
        for (var i = 1; i <= count; i++)
        {
            if (i > 1) sb.Append("===\n");
            sb.Append($"Column {i} content\n");
        }
        return sb.Append(":::\n").ToString();
    }

    /// <summary>:::smartart block; each step becomes a "- " bullet.</summary>
    public static string SmartArt(string type, IEnumerable<string> steps) =>
        BulletedBlock($":::smartart type=\"{Or(type, "process")}\"", steps, "Step 1");

    /// <summary>:::timeline block from "year: label" entries.</summary>
    public static string Timeline(IEnumerable<string> entries) =>
        BulletedBlock(":::timeline", entries, "2026: Milestone");

    /// <summary>:::workflow block; each step becomes a "- " bullet.</summary>
    public static string Workflow(IEnumerable<string> steps) =>
        BulletedBlock(":::workflow", steps, "Step 1");

    /// <summary>:::tabs block; one "=== title" section per line, numbered placeholder content.</summary>
    public static string Tabs(IEnumerable<string> titles)
    {
        var list = Clean(titles).ToList();
        if (list.Count == 0) { list.Add("Tab 1"); list.Add("Tab 2"); }
        var sb = new StringBuilder("\n:::tabs\n");
        var i = 1;
        foreach (var raw in list)
        {
            var title = raw.StartsWith("=== ", StringComparison.Ordinal) ? raw[4..] : raw;
            sb.Append($"=== {title}\nContent {i}\n");
            i++;
        }
        return sb.Append(":::\n").ToString();
    }

    /// <summary>:::embed block for a video/web provider.</summary>
    public static string Embed(string provider, string url) =>
        $"\n:::embed provider=\"{Or(provider, "youtube")}\" src=\"{Or(url, "https://www.youtube.com/watch?v=EXAMPLE_ID")}\"\n:::\n";

    /// <summary>:::references bibliography entry; empty fields fall back to the placeholders.</summary>
    public static string References(string id, string author, string title, string year) =>
        "\n:::references\n" +
        $"@{Or(id, "paper-id")}\n" +
        $"author: {Or(author, "Author Name")}\n" +
        $"title: {Or(title, "Publication Title")}\n" +
        $"year: {Or(year, "2026")}\n" +
        ":::\n";

    /// <summary>:::datagrid block; the first line is the header row.</summary>
    public static string Datagrid(IEnumerable<string> rows)
    {
        var list = Clean(rows).ToList();
        if (list.Count == 0) list.AddRange(new[] { "label,value", "Q1,10", "Q2,25" });
        var sb = new StringBuilder("\n:::datagrid\n");
        foreach (var row in list) sb.Append(row).Append('\n');
        return sb.Append(":::\n").ToString();
    }

    /// <summary>:::canvas SVG scaffold scaled to the requested size.</summary>
    public static string Canvas(int width, int height)
    {
        width = Math.Clamp(width, 10, 4000);
        height = Math.Clamp(height, 10, 4000);
        var cx = width / 2;
        var cy = height / 2;
        var r = (int)(Math.Min(width, height) * 0.4);
        return "\n:::canvas\n" +
               $"<svg viewBox=\"0 0 {width} {height}\" width=\"{width}\" height=\"{height}\">\n" +
               $"  <circle cx=\"{cx}\" cy=\"{cy}\" r=\"{r}\" stroke=\"black\" stroke-width=\"3\" fill=\"red\" />\n" +
               "</svg>\n:::\n";
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string BulletedBlock(string fence, IEnumerable<string> lines, string fallback)
    {
        var list = Clean(lines).ToList();
        if (list.Count == 0) list.Add(fallback);
        var sb = new StringBuilder($"\n{fence}\n");
        foreach (var raw in list)
        {
            var item = raw.StartsWith("- ", StringComparison.Ordinal) ? raw[2..] : raw;
            sb.Append("- ").Append(item).Append('\n');
        }
        return sb.Append(":::\n").ToString();
    }

    private static IEnumerable<string> Clean(IEnumerable<string>? lines) =>
        (lines ?? Array.Empty<string>()).Select(l => l.Trim()).Where(l => l.Length > 0);

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
