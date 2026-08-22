using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Translation
{
    public sealed class DocumentTranslationCoordinator
    {
        // Compiled once — ExtractSections used to rebuild this interpreted regex on every call.
        private static readonly Regex HeadingRe = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

        public sealed record SectionBlock(int Level, string HeadingText, string BodyContent);

        public sealed record TranslationAlignment(
            string SourceHeading,
            string? TargetHeading,
            string SourceBody,
            string? TargetBody,
            bool IsTranslated);

        public sealed record TranslationSyncReport(
            string SourceLanguage,
            string TargetLanguage,
            double CompletenessScore,
            int TotalSections,
            int TranslatedSections,
            List<TranslationAlignment> Alignments);

        public List<SectionBlock> ExtractSections(string markdown)
        {
            var sections = new List<SectionBlock>();
            if (string.IsNullOrWhiteSpace(markdown)) return sections;

            var matches = HeadingRe.Matches(markdown);

            if (matches.Count == 0)
            {
                sections.Add(new SectionBlock(1, "Document Content", markdown.Trim()));
                return sections;
            }

            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                int level = match.Groups[1].Length;
                string heading = match.Groups[2].Value.Trim();

                int start = match.Index + match.Length;
                int end = (i < matches.Count - 1) ? matches[i + 1].Index : markdown.Length;

                string body = (end > start) ? markdown[start..end].Trim() : "";
                sections.Add(new SectionBlock(level, heading, body));
            }

            return sections;
        }

        public TranslationSyncReport AlignTranslations(string sourceMarkdown, string targetMarkdown, string sourceLang = "en", string targetLang = "es")
        {
            var sourceSections = ExtractSections(sourceMarkdown);
            var targetSections = ExtractSections(targetMarkdown);

            var alignments = new List<TranslationAlignment>();
            int translatedCount = 0;

            for (int i = 0; i < sourceSections.Count; i++)
            {
                var src = sourceSections[i];
                var tgt = (i < targetSections.Count) ? targetSections[i] : null;

                bool isTranslated = tgt != null && !string.IsNullOrWhiteSpace(tgt.BodyContent);
                if (isTranslated) translatedCount++;

                alignments.Add(new TranslationAlignment(
                    src.HeadingText,
                    tgt?.HeadingText,
                    src.BodyContent,
                    tgt?.BodyContent,
                    isTranslated));
            }

            double score = sourceSections.Count > 0 ? (double)translatedCount / sourceSections.Count : 1.0;

            return new TranslationSyncReport(
                sourceLang,
                targetLang,
                score,
                sourceSections.Count,
                translatedCount,
                alignments);
        }
    }
}
