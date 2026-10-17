using System;
using System.Collections.Generic;
using System.Text;

namespace MarkSmith.Services;

/// <summary>
/// The editor's find and replace, as plain text operations: every match of a query, stepping to the
/// next or previous one (and whether that wrapped around the document), and Replace all with the
/// caret carried through the edit. Replace all used to drop the caret at line 1 and scroll the
/// editor to the top, so after replacing a word you had to find your place again.
/// </summary>
public static class TextSearch
{
    /// <summary>Checks whether a match at [index, index + length) forms a whole word boundary.</summary>
    public static bool IsWholeWordBoundary(string text, int index, int length)
    {
        if (index > 0 && IsWordChar(text[index - 1])) return false;
        if (index + length < text.Length && IsWordChar(text[index + length])) return false;
        return true;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Represents a text search match with starting offset and character length.</summary>
    public readonly record struct TextMatch(int Index, int Length);

    /// <summary>Validates whether a regular expression pattern is syntactically sound.</summary>
    public static bool TryValidateRegex(string pattern, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrEmpty(pattern)) return true;
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200));
            return true;
        }
        catch (ArgumentException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>Start offsets and lengths of every non-overlapping match, in document order.</summary>
    public static List<TextMatch> FindMatches(string text, string query, StringComparison comparison, bool matchWholeWord = false, bool useRegex = false)
    {
        var matches = new List<TextMatch>();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text)) return matches;

        if (useRegex)
        {
            try
            {
                var options = comparison == StringComparison.OrdinalIgnoreCase
                    ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                    : System.Text.RegularExpressions.RegexOptions.None;
                var pattern = query;
                if (matchWholeWord)
                {
                    pattern = $@"\b(?:{query})\b";
                }
                var rx = new System.Text.RegularExpressions.Regex(pattern, options, TimeSpan.FromMilliseconds(500));
                var rxMatches = rx.Matches(text);
                foreach (System.Text.RegularExpressions.Match m in rxMatches)
                {
                    if (m.Success && m.Length > 0)
                    {
                        matches.Add(new TextMatch(m.Index, m.Length));
                    }
                }
            }
            catch (ArgumentException)
            {
                // Invalid regex query returns 0 matches
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                // Timeout returns whatever was accumulated
            }
            return matches;
        }

        var idx = text.IndexOf(query, comparison);
        while (idx >= 0)
        {
            if (!matchWholeWord || IsWholeWordBoundary(text, idx, query.Length))
            {
                matches.Add(new TextMatch(idx, query.Length));
                if (idx + query.Length >= text.Length) break;
                idx = text.IndexOf(query, idx + query.Length, comparison);
            }
            else
            {
                if (idx + 1 >= text.Length) break;
                idx = text.IndexOf(query, idx + 1, comparison);
            }
        }
        return matches;
    }

    /// <summary>Start offsets of every non-overlapping match, in document order.</summary>
    public static List<int> FindAll(string text, string query, StringComparison comparison, bool matchWholeWord = false, bool useRegex = false)
    {
        var matches = FindMatches(text, query, comparison, matchWholeWord, useRegex);
        var indices = new List<int>(matches.Count);
        foreach (var m in matches) indices.Add(m.Index);
        return indices;
    }

    /// <summary>
    /// The match after <paramref name="current"/> (or the first one at or after
    /// <paramref name="caret"/> when there is no current match). <c>Wrapped</c> is true when the
    /// step went past the end of the document and continued from the top.
    /// </summary>
    public static (int Index, bool Wrapped) Next(IReadOnlyList<int> matches, int current, int caret)
    {
        if (matches.Count == 0) return (-1, false);
        if (current < 0 || current >= matches.Count)
        {
            for (var i = 0; i < matches.Count; i++)
                if (matches[i] >= caret) return (i, false);
            return (0, true);
        }
        return current + 1 < matches.Count ? (current + 1, false) : (0, matches.Count > 1);
    }

    /// <summary>The match before <paramref name="current"/>; <c>Wrapped</c> when it went past the top.</summary>
    public static (int Index, bool Wrapped) Previous(IReadOnlyList<int> matches, int current, int caret)
    {
        if (matches.Count == 0) return (-1, false);
        if (current < 0 || current >= matches.Count)
        {
            for (var i = matches.Count - 1; i >= 0; i--)
                if (matches[i] < caret) return (i, false);
            return (matches.Count - 1, true);
        }
        return current > 0 ? (current - 1, false) : (matches.Count - 1, matches.Count > 1);
    }

    /// <summary>
    /// Replaces every match. <c>Caret</c> is <paramref name="caret"/> moved through the edit: text
    /// before it that grew or shrank shifts it, and a caret inside a match lands at the start of that
    /// match's replacement.
    /// </summary>
    public static (string Text, int Count, int Caret) ReplaceAll(
        string text, string query, string replacement, StringComparison comparison, int caret, bool matchWholeWord = false, bool useRegex = false)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var matches = FindMatches(text, query, comparison, matchWholeWord, useRegex);
        if (matches.Count == 0) return (text, 0, caret);

        var firstLen = matches[0].Length;
        var sb = new StringBuilder(text.Length + matches.Count * Math.Max(0, replacement.Length - firstLen));
        var idx = 0;
        var newCaret = -1;
        foreach (var found in matches)
        {
            if (newCaret < 0 && caret < found.Index) newCaret = sb.Length + (caret - idx);
            sb.Append(text, idx, found.Index - idx);
            if (newCaret < 0 && caret < found.Index + found.Length) newCaret = sb.Length;
            sb.Append(replacement);
            idx = found.Index + found.Length;
        }
        if (newCaret < 0) newCaret = sb.Length + (caret - idx);
        sb.Append(text, idx, text.Length - idx);
        return (sb.ToString(), matches.Count, newCaret);
    }

    /// <summary>
    /// Maintains an MRU search history list with bounded capacity and deduplication.
    /// </summary>
    public static List<string> AddToSearchHistory(IEnumerable<string>? currentHistory, string? query, int maxCapacity = 10)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(query))
        {
            if (currentHistory != null) list.AddRange(currentHistory);
            return list;
        }

        var trimmed = query.Trim();
        list.Add(trimmed);
        if (currentHistory != null)
        {
            foreach (var item in currentHistory)
            {
                if (!string.Equals(item, trimmed, StringComparison.Ordinal) && list.Count < maxCapacity)
                {
                    list.Add(item);
                }
            }
        }
        return list;
    }

    /// <summary>
    /// Trims an existing search history list to a given capacity while preserving MRU order and removing empty/whitespace entries.
    /// </summary>
    public static List<string> TrimSearchHistory(IEnumerable<string>? history, int maxCapacity = 10)
    {
        var result = new List<string>();
        if (history == null || maxCapacity <= 0) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in history)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            var trimmed = item.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
                if (result.Count >= maxCapacity) break;
            }
        }
        return result;
    }

    /// <summary>
    /// Formats a human-readable summary of search history queries.
    /// </summary>
    public static string FormatSearchHistorySummary(IEnumerable<string>? history)
    {
        if (history == null) return "Search history: Empty";

        var valid = history
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(h => h.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (valid.Count == 0) return "Search history: Empty";

        var countStr = valid.Count == 1 ? "1 query" : $"{valid.Count} queries";
        var preview = string.Join(", ", valid.Take(3).Select(q => $"\"{q}\""));
        if (valid.Count > 3) preview += ", …";

        return $"Search history ({countStr}): [{preview}]";
    }

    /// <summary>
    /// Formats the find match count display (e.g. "3 of 12", "1 of 5 (wrapped)", "No results").
    /// </summary>
    public static string FormatFindMatchStatus(int currentIndex, int totalCount, bool wrapped = false)
    {
        if (totalCount <= 0)
            return "No results";

        if (currentIndex <= 0 || currentIndex > totalCount)
            return totalCount == 1 ? "1 match" : $"{totalCount} matches";

        var baseText = $"{currentIndex} of {totalCount}";
        return wrapped ? $"{baseText} (wrapped)" : baseText;
    }

    /// <summary>
    /// Clamps a target match index within [0, totalCount - 1], returning -1 if totalCount is 0 or negative.
    /// </summary>
    public static int ClampMatchIndex(int targetIndex, int totalCount)
    {
        if (totalCount <= 0) return -1;
        return Math.Clamp(targetIndex, 0, totalCount - 1);
    }

    /// <summary>
    /// Target destination when jumping between search matches.
    /// </summary>
    public enum MatchJumpTarget
    {
        First,
        Last
    }

    /// <summary>
    /// Calculates the match index for a quick jump target (First or Last match).
    /// Returns (-1, false) when totalCount is 0 or negative.
    /// </summary>
    public static (int Index, bool Valid) JumpTo(int totalCount, MatchJumpTarget target)
    {
        if (totalCount <= 0) return (-1, false);
        return target switch
        {
            MatchJumpTarget.First => (0, true),
            MatchJumpTarget.Last => (totalCount - 1, true),
            _ => (-1, false)
        };
    }

    /// <summary>
    /// Computes the 1-based line number and column offset for a match index in text.
    /// </summary>
    public static (int Line, int Column, int Length) GetMatchLocation(string text, int matchIndex, int matchLength)
    {
        if (string.IsNullOrEmpty(text) || matchIndex < 0 || matchIndex >= text.Length)
            return (1, 1, Math.Max(0, matchLength));

        int line = 1;
        int lastLineStart = 0;
        for (int i = 0; i < matchIndex; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lastLineStart = i + 1;
            }
        }
        int col = (matchIndex - lastLineStart) + 1;
        return (line, col, Math.Max(0, matchLength));
    }

    /// <summary>
    /// Formats a comprehensive inspector summary for a search match including 1-based index, line number, column, and length.
    /// </summary>
    public static string FormatMatchInspectorSummary(string text, int matchIndex, int matchLength, int matchNumber, int totalCount)
    {
        if (totalCount <= 0 || matchIndex < 0 || string.IsNullOrEmpty(text) || matchIndex >= text.Length)
            return "No match selected";

        var (line, col, len) = GetMatchLocation(text, matchIndex, matchLength);
        var baseInfo = matchNumber > 0 && totalCount > 0
            ? $"Match {matchNumber} of {totalCount}"
            : "Match";
        return $"{baseInfo} · Line {line}, Col {col} (Len: {len})";
    }

    /// <summary>
    /// Formats a contextual snippet around a match index (e.g. "...prefix [MATCH] suffix...").
    /// Replaces newline sequences with single spaces to produce a clean single-line preview.
    /// </summary>
    public static string FormatMatchContextSnippet(string text, int matchIndex, int matchLength, int contextRadius = 15)
    {
        if (string.IsNullOrEmpty(text) || matchIndex < 0 || matchIndex >= text.Length || matchLength <= 0)
            return "No match context available";

        int clampedLen = Math.Min(matchLength, text.Length - matchIndex);
        var matchWord = text.Substring(matchIndex, clampedLen).Replace("\r", " ").Replace("\n", " ").Trim();

        int prefixStart = Math.Max(0, matchIndex - contextRadius);
        int prefixLength = matchIndex - prefixStart;
        var prefix = text.Substring(prefixStart, prefixLength).Replace("\r", " ").Replace("\n", " ").TrimStart();
        if (prefixStart > 0) prefix = "..." + prefix;

        int suffixStart = matchIndex + clampedLen;
        int suffixLength = Math.Min(contextRadius, text.Length - suffixStart);
        var suffix = suffixLength > 0
            ? text.Substring(suffixStart, suffixLength).Replace("\r", " ").Replace("\n", " ").TrimEnd()
            : string.Empty;
        if (suffixStart + suffixLength < text.Length) suffix = suffix + "...";

        var prefixPart = string.IsNullOrEmpty(prefix) ? "" : prefix + " ";
        var suffixPart = string.IsNullOrEmpty(suffix) ? "" : " " + suffix;
        return $"{prefixPart}[{matchWord}]{suffixPart}".Trim();
    }

    /// <summary>
    /// Normalizes and unwraps text within a match (e.g. collapsing internal whitespace and newlines to single spaces).
    /// </summary>
    public static string UnwrapMatchText(string matchText)
    {
        if (string.IsNullOrWhiteSpace(matchText)) return string.Empty;
        var parts = matchText.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Extracts the full single line in text containing the match at matchIndex, prefixing it with the 1-based line number.
    /// </summary>
    public static string FormatMatchLineContext(string text, int matchIndex, int matchLength)
    {
        if (string.IsNullOrEmpty(text) || matchIndex < 0 || matchIndex >= text.Length)
            return "No line context available";

        var (line, col, _) = GetMatchLocation(text, matchIndex, matchLength);
        int lineStart = matchIndex;
        while (lineStart > 0 && text[lineStart - 1] != '\n') lineStart--;

        int lineEnd = matchIndex + Math.Max(0, matchLength);
        while (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n') lineEnd++;

        string lineContent = text.Substring(lineStart, lineEnd - lineStart).Trim();
        return $"L{line} (Col {col}): {lineContent}";
    }

    /// <summary>
    /// Formats a complete search query diagnostics summary including match count, option flags, and regex syntax validation.
    /// </summary>
    public static string FormatSearchQueryDiagnostics(
        string? query,
        int matchCount,
        bool isCaseSensitive,
        bool isWholeWord,
        bool isRegex)
    {
        if (string.IsNullOrEmpty(query)) return "Empty query (0 matches)";

        var optionsList = new List<string>();
        if (isCaseSensitive) optionsList.Add("Match Case");
        if (isWholeWord) optionsList.Add("Whole Word");
        if (isRegex) optionsList.Add("Regex");

        string optionsStr = optionsList.Count > 0
            ? " · Options: [" + string.Join(", ", optionsList) + "]"
            : " · Options: [Default]";

        string matchLabel = matchCount == 1 ? "1 match" : $"{matchCount} matches";

        string modeAndValidation;
        if (isRegex)
        {
            try
            {
                var options = System.Text.RegularExpressions.RegexOptions.None;
                if (!isCaseSensitive) options |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                _ = new System.Text.RegularExpressions.Regex(query, options);
                modeAndValidation = "Mode: Regular Expression (Valid)";
            }
            catch (ArgumentException ex)
            {
                var firstLine = ex.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0];
                modeAndValidation = $"Mode: Regular Expression (Invalid: {firstLine})";
            }
        }
        else
        {
            modeAndValidation = "Mode: Plain Text";
        }

        return $"Query: \"{query}\" · {matchLabel}{optionsStr} · {modeAndValidation}";
    }

    /// <summary>
    /// Formats a search match density and frequency summary relative to total word and character count.
    /// </summary>
    public static string FormatSearchMatchRate(int matchCount, int totalWordCount, int totalCharCount)
    {
        if (matchCount <= 0)
        {
            return totalWordCount > 0
                ? $"0 matches across {totalWordCount:N0} words (0.00% density)"
                : "0 matches (0.00% density)";
        }

        string matchLabel = matchCount == 1 ? "1 match" : $"{matchCount:N0} matches";

        if (totalWordCount <= 0)
        {
            return $"{matchLabel} (Word count unavailable)";
        }

        double densityPct = (double)matchCount / totalWordCount * 100.0;
        int wordFrequency = (int)Math.Round((double)totalWordCount / matchCount);
        string freqStr = wordFrequency <= 1 ? "every word" : $"1 every {wordFrequency:N0} words";

        return $"{matchLabel} across {totalWordCount:N0} words ({densityPct:0.00}% density · {freqStr})";
    }

    /// <summary>
    /// Represents a cluster / hotspot of search matches within a document.
    /// </summary>
    public sealed record SearchMatchCluster(int StartOffset, int EndOffset, int MatchCount);

    /// <summary>
    /// Groups match character offsets into clusters where adjacent matches are separated by no more than <paramref name="clusterDistance"/> characters.
    /// </summary>
    public static List<SearchMatchCluster> ClusterSearchMatches(IEnumerable<int>? matchOffsets, int clusterDistance = 200)
    {
        var clusters = new List<SearchMatchCluster>();
        if (matchOffsets == null) return clusters;

        var sorted = matchOffsets.OrderBy(o => o).ToList();
        if (sorted.Count == 0) return clusters;

        int currentStart = sorted[0];
        int currentEnd = sorted[0];
        int currentCount = 1;

        for (int i = 1; i < sorted.Count; i++)
        {
            int offset = sorted[i];
            if (offset - currentEnd <= clusterDistance)
            {
                currentEnd = offset;
                currentCount++;
            }
            else
            {
                clusters.Add(new SearchMatchCluster(currentStart, currentEnd, currentCount));
                currentStart = offset;
                currentEnd = offset;
                currentCount = 1;
            }
        }

        clusters.Add(new SearchMatchCluster(currentStart, currentEnd, currentCount));
        return clusters;
    }

    /// <summary>
    /// Formats a search match clustering and hotspots summary detailing match concentration and document dispersion.
    /// </summary>
    public static string FormatSearchMatchHotspots(IEnumerable<int>? matchOffsets, int clusterDistance = 200, int totalDocLength = 0)
    {
        var clusters = ClusterSearchMatches(matchOffsets, clusterDistance);
        if (clusters.Count == 0) return "0 matches (No clusters)";

        int totalMatches = clusters.Sum(c => c.MatchCount);
        string matchLabel = totalMatches == 1 ? "1 match" : $"{totalMatches} matches";

        var hotspots = clusters.Where(c => c.MatchCount >= 2).OrderByDescending(c => c.MatchCount).ToList();
        if (hotspots.Count == 0)
        {
            return $"{matchLabel} across {clusters.Count} isolated locations (Evenly dispersed · 0 hotspots)";
        }

        var hotspotPreviews = hotspots.Take(3).Select(h => $"{h.MatchCount} matches @ char {h.StartOffset}–{h.EndOffset}");
        string hotspotList = string.Join(", ", hotspotPreviews);
        if (hotspots.Count > 3) hotspotList += ", …";

        string clusterLabel = clusters.Count == 1 ? "1 cluster" : $"{clusters.Count} clusters";
        string hotspotSummary = hotspots.Count == 1 ? "1 hotspot" : $"{hotspots.Count} hotspots";

        string docSpan = totalDocLength > 0 ? $" (Doc len: {totalDocLength:N0} chars)" : "";
        return $"{matchLabel} in {clusterLabel} ({hotspotSummary}: {hotspotList}){docSpan}";
    }
}

