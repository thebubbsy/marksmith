using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Audio
{
    public sealed class VoiceNoteIngestService
    {
        public sealed record SpeechSegment(TimeSpan Start, TimeSpan End, string Text, string? Speaker);

        public sealed record TranscriptionResult(
            string Title,
            TimeSpan TotalDuration,
            List<ChapterSection> Chapters,
            List<string> KeyTakeaways,
            string FormattedMarkdown);

        public sealed record ChapterSection(
            string Title,
            TimeSpan StartTime,
            List<SpeechSegment> Segments,
            string Content);

        public TranscriptionResult IngestVtt(string vttContent, string defaultTitle = "Voice Recording & Audio Note")
        {
            var segments = ParseVttSegments(vttContent);
            return BuildResultFromSegments(segments, defaultTitle);
        }

        public TranscriptionResult IngestSrt(string srtContent, string defaultTitle = "Audio Transcription")
        {
            var segments = ParseSrtSegments(srtContent);
            return BuildResultFromSegments(segments, defaultTitle);
        }

        // Compiled once — ParseVttSegments used to rebuild this timestamp regex on every ingest.
        private static readonly Regex VttTimeRe = new(@"((?:[0-9]{2}:)?[0-9]{2}:[0-9]{2}[\.,][0-9]{3})\s*-->\s*((?:[0-9]{2}:)?[0-9]{2}:[0-9]{2}[\.,][0-9]{3})", RegexOptions.Compiled);

        private List<SpeechSegment> ParseVttSegments(string vtt)
        {
            var segments = new List<SpeechSegment>();
            if (string.IsNullOrWhiteSpace(vtt)) return segments;

            var lines = vtt.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                var match = VttTimeRe.Match(lines[i]);
                if (match.Success)
                {
                    var start = ParseTimestamp(match.Groups[1].Value);
                    var end = ParseTimestamp(match.Groups[2].Value);
                    var textBuilder = new StringBuilder();

                    i++;
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !lines[i].Contains("-->"))
                    {
                        textBuilder.AppendLine(lines[i].Trim());
                        i++;
                    }

                    string text = textBuilder.ToString().Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        segments.Add(new SpeechSegment(start, end, text, null));
                    }
                }
            }
            return segments;
        }

        private List<SpeechSegment> ParseSrtSegments(string srt)
        {
            return ParseVttSegments(srt); // Regex handles both comma and dot milliseconds
        }

        private TimeSpan ParseTimestamp(string ts)
        {
            ts = ts.Replace(',', '.');
            var parts = ts.Split(':');
            if (parts.Length == 3)
            {
                if (double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double h) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double m) &&
                    double.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s))
                {
                    return TimeSpan.FromHours(h) + TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
                }
            }
            else if (parts.Length == 2)
            {
                if (double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double m) &&
                    double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s))
                {
                    return TimeSpan.FromMinutes(m) + TimeSpan.FromSeconds(s);
                }
            }
            return TimeSpan.Zero;
        }

        private TranscriptionResult BuildResultFromSegments(List<SpeechSegment> segments, string title)
        {
            if (segments.Count == 0)
            {
                return new TranscriptionResult(title, TimeSpan.Zero, new List<ChapterSection>(), new List<string>(), "# " + title + "\n\n*No speech audio detected.*");
            }

            var chapters = new List<ChapterSection>();
            var currentChapterSegments = new List<SpeechSegment>();
            TimeSpan chapterStart = segments[0].Start;
            int chapterIndex = 1;

            for (int i = 0; i < segments.Count; i++)
            {
                currentChapterSegments.Add(segments[i]);

                // Create chapter break if silence pause is > 4.0 seconds or chapter length reaches 3 minutes
                bool isLast = i == segments.Count - 1;
                bool isPauseBreak = !isLast && (segments[i + 1].Start - segments[i].End).TotalSeconds > 4.0;
                bool isTimeBreak = (segments[i].End - chapterStart).TotalMinutes >= 3.0;

                if (isLast || isPauseBreak || isTimeBreak)
                {
                    var text = string.Join(" ", currentChapterSegments.Select(s => s.Text));
                    string chapterTitle = $"Chapter {chapterIndex}: Discussion [{chapterStart:mm\\:ss}]";
                    chapters.Add(new ChapterSection(chapterTitle, chapterStart, new List<SpeechSegment>(currentChapterSegments), text));

                    currentChapterSegments.Clear();
                    chapterIndex++;
                    if (!isLast) chapterStart = segments[i + 1].Start;
                }
            }

            // Extract bullet takeaways
            var takeaways = segments
                .Where(s => s.Text.Length > 20 && (s.Text.Contains("need to") || s.Text.Contains("will") || s.Text.Contains("important") || s.Text.Contains("should")))
                .Select(s => s.Text.Trim())
                .Take(5)
                .ToList();

            if (takeaways.Count == 0 && segments.Count > 0)
            {
                takeaways = segments.Take(3).Select(s => s.Text).ToList();
            }

            // Build Markdown
            var sb = new StringBuilder();
            sb.AppendLine($"# 🎙️ {title}");
            sb.AppendLine();
            sb.AppendLine($"> **Total Audio Duration**: `{segments.Last().End:hh\\:mm\\:ss}` · **Chapters**: `{chapters.Count}`");
            sb.AppendLine();

            if (takeaways.Count > 0)
            {
                sb.AppendLine("## 📌 Key Takeaways & Action Items");
                foreach (var t in takeaways)
                {
                    sb.AppendLine($"- {t}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();

            foreach (var ch in chapters)
            {
                sb.AppendLine($"## ⏱️ [{ch.StartTime:mm\\:ss}] {ch.Title}");
                sb.AppendLine();
                sb.AppendLine(ch.Content);
                sb.AppendLine();
            }

            return new TranscriptionResult(
                title,
                segments.Last().End,
                chapters,
                takeaways,
                sb.ToString().TrimEnd());
        }
    }
}
