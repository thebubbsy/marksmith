using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Services
{
    public class CitationEntry
    {
        public string Key { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public int Year { get; set; }
        public string Publisher { get; set; } = string.Empty;
    }

    public class CitationProcessResult
    {
        public string ProcessedMarkdown { get; set; } = string.Empty;
        public List<CitationEntry> UsedCitations { get; set; } = new List<CitationEntry>();
    }

    public class CitationEngineService
    {
        private static readonly Regex CitationClusterRegex = new(@"\[@([\w\-]+(?:;\s*@[\w\-]+)*)\]", RegexOptions.Compiled);

        public CitationProcessResult Process(string markdown, Dictionary<string, CitationEntry> library)
        {
            var result = new CitationProcessResult();
            if (string.IsNullOrEmpty(markdown))
            {
                result.ProcessedMarkdown = markdown ?? string.Empty;
                return result;
            }

            if (!markdown.Contains("[@", StringComparison.Ordinal))
            {
                result.ProcessedMarkdown = markdown;
                return result;
            }

            var usedKeys = new List<string>();
            var keyToIndexMap = new Dictionary<string, int>();

            // Match [@citekey1; @citekey2] or [@citekey]
            string processed = CitationClusterRegex.Replace(markdown, match =>
            {
                string rawKeys = match.Groups[1].Value;
                string[] keys = rawKeys.Split(';');
                var formattedNumbers = new List<string>();

                foreach (var rawKey in keys)
                {
                    string key = rawKey.Trim().TrimStart('@');
                    if (!keyToIndexMap.ContainsKey(key))
                    {
                        usedKeys.Add(key);
                        keyToIndexMap[key] = usedKeys.Count;
                    }
                    formattedNumbers.Add(keyToIndexMap[key].ToString());
                }

                return $"[{string.Join(", ", formattedNumbers)}]";
            });

            var bibliographyBuilder = new StringBuilder();
            if (usedKeys.Count > 0)
            {
                bibliographyBuilder.AppendLine("\n\n## References\n");
                foreach (var key in usedKeys)
                {
                    int index = keyToIndexMap[key];
                    if (library != null && library.TryGetValue(key, out var entry))
                    {
                        result.UsedCitations.Add(entry);
                        string pubStr = string.IsNullOrWhiteSpace(entry.Publisher) ? "" : $", {entry.Publisher}";
                        bibliographyBuilder.AppendLine($"{index}. {entry.Author} ({entry.Year}). *{entry.Title}*{pubStr}.");
                    }
                    else
                    {
                        bibliographyBuilder.AppendLine($"{index}. [@{key}]");
                    }
                }
            }

            result.ProcessedMarkdown = processed + bibliographyBuilder.ToString();
            return result;
        }
    }
}
