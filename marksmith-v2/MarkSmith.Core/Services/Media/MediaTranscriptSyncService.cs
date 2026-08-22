using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Media;

public record TranscriptCue(
    double StartSeconds,
    string TimestampText,
    string SpokenText,
    int LineIndex);

public class SyncedMediaBlock
{
    public string MediaType { get; set; } = "audio";
    public string MediaUrl { get; set; } = string.Empty;
    public string? Title { get; set; }
    public List<TranscriptCue> Cues { get; } = new();
}

/// <summary>
/// Service for parsing media fence containers and generating timestamp-synchronized interactive transcript readers.
/// </summary>
public static class MediaTranscriptSyncService
{
    private static readonly Regex MediaFenceRegex = new(
        @":::(audio|video)(?:\s+url=""([^""]+)"")?(?:\s+title=""([^""]+)"")?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    private static readonly Regex TimestampLineRegex = new(
        @"^\[(\d{1,2}:\d{2}(?::\d{2})?(?:\.\d+)?)\]\s*(.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts all synchronized media blocks from Markdown.
    /// </summary>
    public static List<SyncedMediaBlock> ExtractMediaBlocks(string markdown)
    {
        var list = new List<SyncedMediaBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
            return list;

        foreach (Match m in MediaFenceRegex.Matches(markdown))
        {
            string type = m.Groups[1].Value.ToLowerInvariant();
            string url = m.Groups[2].Success ? m.Groups[2].Value : "";
            string? title = m.Groups[3].Success ? m.Groups[3].Value : null;
            string body = m.Groups[4].Value;

            var block = new SyncedMediaBlock
            {
                MediaType = type,
                MediaUrl = url,
                Title = title
            };

            var lines = body.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            int idx = 1;
            foreach (var line in lines)
            {
                var tsMatch = TimestampLineRegex.Match(line.Trim());
                if (tsMatch.Success)
                {
                    string tsStr = tsMatch.Groups[1].Value;
                    string text = tsMatch.Groups[2].Value;
                    double sec = ParseTimestamp(tsStr);
                    block.Cues.Add(new TranscriptCue(sec, tsStr, text, idx++));
                }
            }

            list.Add(block);
        }

        return list;
    }

    /// <summary>
    /// Renders an interactive HTML5 media player container with synchronized transcript highlight cues.
    /// </summary>
    public static string RenderSyncedPlayerHtml(SyncedMediaBlock media)
    {
        var sb = new StringBuilder();
        string encodedUrl = System.Net.WebUtility.HtmlEncode(media.MediaUrl);
        string playerTag = media.MediaType == "video"
            ? $"<video controls class=\"ms-sync-video\" src=\"{encodedUrl}\"></video>"
            : $"<audio controls class=\"ms-sync-audio\" src=\"{encodedUrl}\"></audio>";

        string titleHtml = !string.IsNullOrEmpty(media.Title)
            ? $"<div class=\"ms-media-title\">{System.Net.WebUtility.HtmlEncode(media.Title)}</div>"
            : "";

        sb.AppendLine($"<div class=\"ms-synced-media-container\" data-type=\"{media.MediaType}\">");
        if (!string.IsNullOrEmpty(titleHtml)) sb.AppendLine($"  {titleHtml}");
        sb.AppendLine($"  <div class=\"ms-player-wrapper\">{playerTag}</div>");
        sb.AppendLine("  <div class=\"ms-transcript-timeline\">");

        foreach (var cue in media.Cues)
        {
            sb.AppendLine($"    <div class=\"ms-cue-row\" data-time=\"{cue.StartSeconds}\" onclick=\"seekMediaTo({cue.StartSeconds})\">");
            sb.AppendLine($"      <span class=\"ms-cue-ts\">[{cue.TimestampText}]</span>");
            sb.AppendLine($"      <span class=\"ms-cue-text\">{System.Net.WebUtility.HtmlEncode(cue.SpokenText)}</span>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    private static double ParseTimestamp(string ts)
    {
        var parts = ts.Split(':');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double m) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s))
        {
            return m * 60 + s;
        }
        if (parts.Length == 3 &&
            double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double h) &&
            double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double min) &&
            double.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double sec))
        {
            return h * 3600 + min * 60 + sec;
        }
        return 0;
    }
}
