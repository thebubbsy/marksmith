using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Ocr;

/// <summary>
/// Reclassifies OCR detected blocks and visual elements into different structural Markdown interpretations
/// (e.g., Picture <-> ShapeMosaic <-> Diagram <-> Table <-> Key-Value <-> Heading <-> Text).
/// Used by the interactive OCR Inspection Studio to allow users to flick through scanned pages and override
/// engine classifications before finalizing the import into the document.
/// </summary>
public static class OcrBlockReclassifier
{
    private static readonly Regex ImageRegex = new(@"!\[(.*?)\]\((.*?)\)", RegexOptions.Compiled);
    private static readonly Regex ContainerFenceRegex = new(@"^:::[a-zA-Z0-9_\-= ]*\r?\n([\s\S]*?)\r?\n:::", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex CodeFenceRegex = new(@"^```[a-zA-Z0-9_-]*\r?\n([\s\S]*?)\r?\n```", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex HeadingPrefixRegex = new(@"^#{1,6}\s*", RegexOptions.Compiled);
    private static readonly Regex ListPrefixRegex = new(@"^(\s*[-*+]\s+|\s*\d+[.)]\s+)", RegexOptions.Compiled);
    private static readonly Regex TablePipesRegex = new(@"^\||\|$", RegexOptions.Compiled);

    /// <summary>
    /// Reclassifies a block into the target kind, transforming its Markdown representation accordingly.
    /// </summary>
    public static string Reclassify(
        OcrBlockKind targetKind,
        OcrBlockKind currentKind,
        string currentMarkdown,
        IReadOnlyList<OcrTraceWord>? words,
        int pageNumber,
        int blockIndex,
        OcrNormRect box)
    {
        if (targetKind == currentKind && !string.IsNullOrWhiteSpace(currentMarkdown))
        {
            return currentMarkdown.Trim();
        }

        words ??= Array.Empty<OcrTraceWord>();
        string plainText = ExtractPlainText(currentMarkdown, words);

        return targetKind switch
        {
            OcrBlockKind.Picture => FormatAsPicture(currentMarkdown, plainText, pageNumber, blockIndex),
            OcrBlockKind.ShapeMosaic => FormatAsMosaic(currentMarkdown, plainText, words, box, pageNumber, blockIndex),
            OcrBlockKind.Diagram => FormatAsDiagram(currentMarkdown, plainText, words),
            OcrBlockKind.Table => FormatAsTable(plainText, words),
            OcrBlockKind.KeyValue => FormatAsKeyValue(plainText, words),
            OcrBlockKind.Heading => FormatAsHeading(plainText, blockIndex),
            OcrBlockKind.List => FormatAsList(plainText, words),
            OcrBlockKind.Paragraph or _ => FormatAsParagraph(plainText),
        };
    }

    /// <summary>
    /// Extracts legible plain text from existing markdown fences or word tokens.
    /// </summary>
    public static string ExtractPlainText(string? markdown, IReadOnlyList<OcrTraceWord>? words)
    {
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            string clean = markdown.Trim();

            // Check if it's an image link: extract alt text
            var imgMatch = ImageRegex.Match(clean);
            if (imgMatch.Success)
            {
                string alt = imgMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(alt) && !alt.StartsWith("Picture from page", StringComparison.OrdinalIgnoreCase))
                {
                    return alt;
                }
            }

            // Strip container fence :::shapes ... :::
            var containerMatch = ContainerFenceRegex.Match(clean);
            if (containerMatch.Success)
            {
                string body = containerMatch.Groups[1].Value;
                // If it contains # mermaid: ...
                var lines = body.Split('\n').Select(l => l.Trim()).Where(l => !string.IsNullOrEmpty(l)).ToList();
                var textParts = new List<string>();
                foreach (var line in lines)
                {
                    if (line.StartsWith("#") || line.StartsWith(":::") || line.StartsWith("%%")) continue;
                    var matchText = Regex.Match(line, @"text=""(.*?)""");
                    if (matchText.Success)
                    {
                        textParts.Add(matchText.Groups[1].Value);
                    }
                }
                if (textParts.Count > 0) return string.Join(" ", textParts);
            }

            // Strip code fence ```mermaid ... ```
            var codeMatch = CodeFenceRegex.Match(clean);
            if (codeMatch.Success)
            {
                string body = codeMatch.Groups[1].Value;
                var nodeLabels = Regex.Matches(body, @"\[""?(.*?)""?\]|\(""?([^)]*?)""?\)");
                var labels = new List<string>();
                foreach (Match m in nodeLabels)
                {
                    string val = m.Groups[1].Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : m.Groups[2].Value;
                    if (!string.IsNullOrWhiteSpace(val)) labels.Add(val.Trim());
                }
                if (labels.Count > 0) return string.Join(" -> ", labels);
            }

            // If it's a markdown table, strip pipes and separator rows
            if (clean.Contains('|'))
            {
                var tableLines = clean.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('|') && l.EndsWith('|')).ToList();
                if (tableLines.Count > 0)
                {
                    var cellValues = new List<string>();
                    foreach (var tl in tableLines)
                    {
                        if (Regex.IsMatch(tl, @"^\|[\s\-:|]+\|$")) continue; // separator
                        var cells = tl.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0);
                        cellValues.AddRange(cells);
                    }
                    if (cellValues.Count > 0) return string.Join(" ", cellValues);
                }
            }

            // Strip heading symbols and list markers
            var normalLines = clean.Split('\n')
                .Select(l => HeadingPrefixRegex.Replace(l.Trim(), ""))
                .Select(l => ListPrefixRegex.Replace(l.Trim(), ""))
                .Where(l => l.Length > 0)
                .ToList();

            if (normalLines.Count > 0)
            {
                string res = string.Join(" ", normalLines).Trim();
                if (res.Length > 0) return res;
            }
        }

        if (words is { Count: > 0 })
        {
            var tokens = words.Select(w => w.Text.Trim()).Where(t => t.Length > 0);
            return string.Join(" ", tokens);
        }

        return string.Empty;
    }

    private static string FormatAsPicture(string currentMarkdown, string plainText, int pageNumber, int blockIndex)
    {
        // Preserve existing image link if present
        var match = ImageRegex.Match(currentMarkdown);
        if (match.Success)
        {
            return match.Value;
        }

        string safeName = $"page{pageNumber}-visual{blockIndex + 1}.png";
        string label = string.IsNullOrWhiteSpace(plainText) ? $"Visual Graphic (Page {pageNumber}, Item {blockIndex + 1})" : plainText;
        if (label.Length > 50) label = label.Substring(0, 47) + "…";
        return $"![{label}]({safeName})";
    }

    private static string FormatAsMosaic(
        string currentMarkdown,
        string plainText,
        IReadOnlyList<OcrTraceWord> words,
        OcrNormRect box,
        int pageNumber,
        int blockIndex)
    {
        if (currentMarkdown.StartsWith(":::shapes compact=true", StringComparison.OrdinalIgnoreCase))
        {
            return currentMarkdown.Trim();
        }

        float x = Math.Clamp(box.X, 0.05f, 0.85f);
        float y = Math.Clamp(box.Y, 0.05f, 0.85f);
        float w = Math.Clamp(box.Width, 0.10f, 0.90f);
        float h = Math.Clamp(box.Height, 0.08f, 0.60f);

        var sb = new StringBuilder();
        sb.AppendLine(":::shapes compact=true");
        sb.AppendLine($"# Shape Mosaic generated from Page {pageNumber} Element {blockIndex + 1}");

        // Generate vector shape tiles covering the box area
        sb.AppendLine($"rect {x:F2},{y:F2},{w:F2},{h:F2} fill=#1E40AF stroke=#3B82F6");

        float innerX = x + w * 0.08f;
        float innerY = y + h * 0.12f;
        float innerW = w * 0.84f;
        float innerH = h * 0.76f;

        string label = string.IsNullOrWhiteSpace(plainText) ? $"Visual Element {blockIndex + 1}" : plainText;
        if (label.Length > 36) label = label.Substring(0, 33) + "…";
        label = label.Replace("\"", "'");

        sb.AppendLine($"roundRect {innerX:F2},{innerY:F2},{innerW:F2},{innerH:F2} fill=#2563EB stroke=#60A5FA text=\"{label}\"");
        sb.Append(":::");
        return sb.ToString();
    }

    private static string FormatAsDiagram(string currentMarkdown, string plainText, IReadOnlyList<OcrTraceWord> words)
    {
        if (currentMarkdown.StartsWith("```mermaid", StringComparison.OrdinalIgnoreCase) ||
            (currentMarkdown.StartsWith(":::shapes", StringComparison.OrdinalIgnoreCase) && !currentMarkdown.Contains("compact=true")))
        {
            return currentMarkdown.Trim();
        }

        var tokens = words.Count > 0
            ? words.Select(w => w.Text.Trim()).Where(w => w.Length > 0).ToList()
            : plainText.Split(new[] { ' ', '-', '>', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        if (tokens.Count == 0)
        {
            tokens = new List<string> { "Start", "Process", "Complete" };
        }

        // Limit to 2 to 5 clean nodes
        var nodeLabels = new List<string>();
        int step = Math.Max(1, tokens.Count / 4);
        for (int i = 0; i < tokens.Count && nodeLabels.Count < 4; i += step)
        {
            string t = tokens[i].Replace("\"", "").Trim();
            if (t.Length > 24) t = t.Substring(0, 21) + "…";
            if (!string.IsNullOrEmpty(t)) nodeLabels.Add(t);
        }

        if (nodeLabels.Count == 1)
        {
            nodeLabels.Add("Output");
        }

        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("flowchart LR");
        for (int idx = 0; idx < nodeLabels.Count; idx++)
        {
            string id = $"N{idx + 1}";
            string nextId = $"N{idx + 2}";
            sb.AppendLine($"    {id}[\"{nodeLabels[idx]}\"]");
            if (idx < nodeLabels.Count - 1)
            {
                sb.AppendLine($"    {id} --> {nextId}");
            }
        }
        sb.Append("```");
        return sb.ToString();
    }

    private static string FormatAsTable(string plainText, IReadOnlyList<OcrTraceWord> words)
    {
        var tokens = words.Count > 0
            ? words.Select(w => w.Text.Trim()).Where(w => w.Length > 0).ToList()
            : plainText.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        if (tokens.Count < 2)
        {
            tokens = new List<string> { "Item", "Value", "Sample", "Data" };
        }

        var sb = new StringBuilder();
        sb.AppendLine("| Key / Column | Value / Metric |");
        sb.AppendLine("| --- | --- |");

        for (int i = 0; i < tokens.Count; i += 2)
        {
            string col1 = tokens[i].Replace("|", "\\|");
            string col2 = (i + 1 < tokens.Count) ? tokens[i + 1].Replace("|", "\\|") : "—";
            sb.AppendLine($"| {col1} | {col2} |");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatAsKeyValue(string plainText, IReadOnlyList<OcrTraceWord> words)
    {
        var tokens = words.Count > 0
            ? words.Select(w => w.Text.Trim()).Where(w => w.Length > 0).ToList()
            : plainText.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        if (tokens.Count < 2)
        {
            tokens = new List<string> { "Property", "Configured", "Status", "Active" };
        }

        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i += 2)
        {
            string key = tokens[i];
            string val = (i + 1 < tokens.Count) ? tokens[i + 1] : "—";
            sb.AppendLine($"- **{key}:** {val}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatAsHeading(string plainText, int blockIndex)
    {
        string h = string.IsNullOrWhiteSpace(plainText) ? $"Section {blockIndex + 1}" : plainText.Replace("\n", " ").Trim();
        if (h.Length > 60) h = h.Substring(0, 57) + "…";
        return $"## {h}";
    }

    private static string FormatAsList(string plainText, IReadOnlyList<OcrTraceWord> words)
    {
        var lines = plainText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count <= 1 && words.Count >= 4)
        {
            // Chunk words into list items
            lines = new List<string>();
            int chunkSize = Math.Max(2, words.Count / 3);
            for (int i = 0; i < words.Count; i += chunkSize)
            {
                var chunk = words.Skip(i).Take(chunkSize).Select(w => w.Text);
                lines.Add(string.Join(" ", chunk));
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("Item 1");
            lines.Add("Item 2");
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.AppendLine($"- {line}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatAsParagraph(string plainText)
    {
        return string.IsNullOrWhiteSpace(plainText) ? "(Empty text block)" : plainText.Trim();
    }
}
