using System;
using System.Text.RegularExpressions;

namespace MdToPdf.Core.Services
{
    public class ReadabilityResult
    {
        public int TotalWords { get; set; }
        public int TotalSentences { get; set; }
        public int TotalSyllables { get; set; }
        public double FleschReadingEase { get; set; }
        public double FleschKincaidGradeLevel { get; set; }
        public string ReadabilityLabel { get; set; } = string.Empty;
    }

    public class ReadabilityAnalyzerService
    {
        public ReadabilityResult Analyze(string markdown)
        {
            var result = new ReadabilityResult();
            if (string.IsNullOrWhiteSpace(markdown)) return result;

            string cleanText = StripMarkdownConstructs(markdown);
            if (string.IsNullOrWhiteSpace(cleanText)) return result;

            var sentences = Regex.Split(cleanText, @"[.!?]+(?:\s+|$)");
            int sentenceCount = 0;
            foreach (var s in sentences)
            {
                if (!string.IsNullOrWhiteSpace(s)) sentenceCount++;
            }
            if (sentenceCount == 0) sentenceCount = 1;
            result.TotalSentences = sentenceCount;

            var words = Regex.Split(cleanText, @"\s+");
            int wordCount = 0;
            int syllableCount = 0;

            foreach (var w in words)
            {
                string cleanWord = Regex.Replace(w, @"[^\w]", "");
                if (!string.IsNullOrEmpty(cleanWord))
                {
                    wordCount++;
                    syllableCount += CountSyllables(cleanWord);
                }
            }

            if (wordCount == 0) return result;

            result.TotalWords = wordCount;
            result.TotalSyllables = syllableCount;

            double wordsPerSentence = (double)wordCount / sentenceCount;
            double syllablesPerWord = (double)syllableCount / wordCount;

            // Flesch Reading Ease formula
            result.FleschReadingEase = Math.Round(206.835 - (1.015 * wordsPerSentence) - (84.6 * syllablesPerWord), 1);

            // Flesch-Kincaid Grade Level formula
            result.FleschKincaidGradeLevel = Math.Round((0.39 * wordsPerSentence) + (11.8 * syllablesPerWord) - 15.59, 1);

            result.ReadabilityLabel = GetReadabilityLabel(result.FleschReadingEase);

            return result;
        }

        private static string StripMarkdownConstructs(string markdown)
        {
            // Strip code blocks
            string text = Regex.Replace(markdown, @"```[\s\S]*?```", "");
            // Strip inline code
            text = Regex.Replace(text, @"`[^`]*`", "");
            // Strip block math
            text = Regex.Replace(text, @"\$\$[\s\S]*?\$\$", "");
            // Strip inline math
            text = Regex.Replace(text, @"\$[^\$]*\$", "");
            // Strip HTML tags
            text = Regex.Replace(text, @"<[^>]+>", "");
            // Strip headers markdown symbols
            text = Regex.Replace(text, @"^[#\-\*\+>]+\s*", "", RegexOptions.Multiline);

            return text;
        }

        private static int CountSyllables(string word)
        {
            word = word.ToLowerInvariant();
            if (word.Length <= 3) return 1;

            word = Regex.Replace(word, @"(?:[^laeiouy]es|ed|[^laeiouy]e)$", "");
            word = Regex.Replace(word, @"^y", "");

            var matches = Regex.Matches(word, @"[aeiouy]{1,2}");
            return Math.Max(1, matches.Count);
        }

        private static string GetReadabilityLabel(double score)
        {
            if (score >= 90.0) return "Very Easy";
            if (score >= 80.0) return "Easy";
            if (score >= 70.0) return "Fairly Easy";
            if (score >= 60.0) return "Standard";
            if (score >= 50.0) return "Fairly Difficult";
            if (score >= 30.0) return "Difficult";
            return "Very Confusing";
        }
    }
}
