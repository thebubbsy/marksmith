using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Audio;

public record VoiceCue(string Speaker, string Text, double EstimatedDurationSeconds, int LineNumber);

public class VoiceScriptReport
{
    public List<VoiceCue> Cues { get; } = new();
    public double TotalEstimatedDurationSeconds => Cues.Sum(c => c.EstimatedDurationSeconds);
    public string FormattedDuration => TimeSpan.FromSeconds(TotalEstimatedDurationSeconds).ToString(@"mm\:ss");
    public string SsmlXml { get; set; } = string.Empty;
}

/// <summary>
/// Service that transforms Markdown documents into phonetically formatted SSML audio narration scripts and voice-over runtimes.
/// </summary>
public static class VoiceOverScriptService
{
    private static readonly Regex SpeakerCueRegex = new(@"^\[Speaker:\s*([^\]]+)\]\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new(@"\b\w+\b", RegexOptions.Compiled);
    private static readonly Regex BulletPrefixRegex = new(@"^[-*+]\s+", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkRegex = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex FormattingStripRegex = new(@"[*_`~]", RegexOptions.Compiled);

    /// <summary>
    /// Parses Markdown and generates structured voice cues and standard W3C SSML markup.
    /// </summary>
    public static VoiceScriptReport GenerateScript(string markdown, int wordsPerMinute = 150)
    {
        var report = new VoiceScriptReport();
        if (string.IsNullOrWhiteSpace(markdown))
            return report;

        var lines = markdown.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        string currentSpeaker = "Narrator";
        var sbSsml = new StringBuilder();

        sbSsml.AppendLine("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\">");

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // 1. Check for Speaker switch: [Speaker: Alice] Hello world
            var speakerMatch = SpeakerCueRegex.Match(line);
            if (speakerMatch.Success)
            {
                currentSpeaker = speakerMatch.Groups[1].Value.Trim();
                line = speakerMatch.Groups[2].Value.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
            }

            // 2. Heading
            var hMatch = HeadingRegex.Match(line);
            if (hMatch.Success)
            {
                string title = hMatch.Groups[2].Value.Trim();
                int words = WordRegex.Count(title);
                double dur = Math.Max(1.0, (words / (double)wordsPerMinute) * 60.0 + 0.8); // extra pause for heading

                report.Cues.Add(new VoiceCue(currentSpeaker, title, dur, lineNum));
                sbSsml.AppendLine($"  <voice name=\"{currentSpeaker}\"><p><s>{System.Net.WebUtility.HtmlEncode(title)}</s><break time=\"800ms\"/></p></voice>");
                continue;
            }

            // 3. Normal paragraph / bullet
            string cleanText = BulletPrefixRegex.Replace(line, "").Trim();
            cleanText = MarkdownLinkRegex.Replace(cleanText, "$1"); // strip markdown links
            cleanText = FormattingStripRegex.Replace(cleanText, ""); // strip markdown formatting

            int bodyWords = WordRegex.Count(cleanText);
            if (bodyWords > 0)
            {
                double duration = Math.Max(0.5, (bodyWords / (double)wordsPerMinute) * 60.0 + 0.4);
                report.Cues.Add(new VoiceCue(currentSpeaker, cleanText, duration, lineNum));
                sbSsml.AppendLine($"  <voice name=\"{currentSpeaker}\"><p><s>{System.Net.WebUtility.HtmlEncode(cleanText)}</s><break time=\"400ms\"/></p></voice>");
            }
        }

        sbSsml.AppendLine("</speak>");
        report.SsmlXml = sbSsml.ToString();
        return report;
    }
}
