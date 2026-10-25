using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Represents an extracted regex match with its captured groups (group index, group name, value).
    /// </summary>
    public sealed record RegexMatchCapture(int MatchIndex, int Offset, int Length, string Value, IReadOnlyList<(int GroupIndex, string GroupName, string Value)> Groups);

    /// <summary>
    /// Extracts captured groups for non-overlapping regex matches in text, up to <paramref name="maxMatches"/>.
    /// </summary>
    public static List<RegexMatchCapture> ExtractRegexCaptureGroups(string text, string pattern, bool ignoreCase = true, int maxMatches = 10)
    {
        var result = new List<RegexMatchCapture>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern) || maxMatches <= 0) return result;

        try
        {
            var options = ignoreCase
                ? System.Text.RegularExpressions.RegexOptions.IgnoreCase
                : System.Text.RegularExpressions.RegexOptions.None;
            var rx = new System.Text.RegularExpressions.Regex(pattern, options, TimeSpan.FromMilliseconds(250));
            var groupNames = rx.GetGroupNames();
            var matches = rx.Matches(text);

            int count = 0;
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (!m.Success || m.Length == 0) continue;

                var groups = new List<(int, string, string)>();
                for (int g = 1; g < m.Groups.Count; g++) // Group 0 is the entire match
                {
                    var grp = m.Groups[g];
                    if (grp.Success)
                    {
                        string name = groupNames.Length > g ? groupNames[g] : g.ToString();
                        groups.Add((g, name, grp.Value));
                    }
                }

                result.Add(new RegexMatchCapture(count + 1, m.Index, m.Length, m.Value, groups));
                count++;
                if (count >= maxMatches) break;
            }
        }
        catch (ArgumentException)
        {
            // Invalid regex query
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // Timeout returns whatever was accumulated
        }

        return result;
    }

    /// <summary>
    /// Formats a human-readable summary of regex matches and their captured groups for inspection or clipboard export.
    /// </summary>
    public static string FormatRegexCaptureGroupsSummary(string text, string pattern, bool ignoreCase = true, int maxMatches = 5)
    {
        if (string.IsNullOrEmpty(pattern)) return "Regex capture groups: (No query specified)";
        if (string.IsNullOrEmpty(text)) return $"Regex capture groups for '{pattern}': (Document is empty)";

        var captures = ExtractRegexCaptureGroups(text, pattern, ignoreCase, maxMatches);
        if (captures.Count == 0) return $"Regex capture groups for '{pattern}': 0 matches";

        var sb = new StringBuilder();
        int totalGroups = captures.Sum(c => c.Groups.Count);
        string matchCountStr = captures.Count == 1 ? "1 match" : $"{captures.Count} matches";

        if (totalGroups == 0)
        {
            sb.Append($"Regex matches for '{pattern}' ({matchCountStr}, 0 capturing groups): ");
            var values = captures.Select(c => $"\"{c.Value}\" @ char {c.Offset}");
            sb.Append(string.Join(", ", values));
            return sb.ToString();
        }

        sb.AppendLine($"Regex capture groups for '{pattern}' ({matchCountStr}, {totalGroups} captured tokens):");
        for (int i = 0; i < captures.Count; i++)
        {
            var c = captures[i];
            sb.Append($"  Match {c.MatchIndex} (\"{c.Value}\" @ {c.Offset}): ");
            var grpStrs = c.Groups.Select(g => g.GroupName == g.GroupIndex.ToString() ? $"[{g.GroupIndex}]=\"{g.Value}\"" : $"[{g.GroupName}]=\"{g.Value}\"");
            sb.Append(string.Join(", ", grpStrs));
            if (i < captures.Count - 1) sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Transforms <paramref name="replacement"/> to mirror the capitalization pattern of <paramref name="sourceSample"/>
    /// (ALL CAPS, TitleCase/PascalCase, or lowercase).
    /// </summary>
    public static string ApplyCasePattern(string sourceSample, string replacement)
    {
        if (string.IsNullOrEmpty(sourceSample) || string.IsNullOrEmpty(replacement))
            return replacement ?? string.Empty;

        bool hasLetters = false;
        bool allUpper = true;
        bool allLower = true;
        int letterCount = 0;

        foreach (char c in sourceSample)
        {
            if (char.IsLetter(c))
            {
                hasLetters = true;
                letterCount++;
                if (!char.IsUpper(c)) allUpper = false;
                if (!char.IsLower(c)) allLower = false;
            }
        }

        if (!hasLetters) return replacement;

        if (allUpper && letterCount > 1)
        {
            return replacement.ToUpperInvariant();
        }

        if (allLower)
        {
            return replacement.ToLowerInvariant();
        }

        if (char.IsUpper(sourceSample[0]))
        {
            bool restLower = true;
            for (int i = 1; i < sourceSample.Length; i++)
            {
                if (char.IsLetter(sourceSample[i]) && !char.IsLower(sourceSample[i]))
                {
                    restLower = false;
                    break;
                }
            }

            if (restLower)
            {
                // Pure TitleCase: capitalize first char, lowercase remainder unless replacement itself has mixed case
                bool replacementHasInternalUpper = replacement.Skip(1).Any(char.IsUpper) && replacement.Any(char.IsLower);
                if (replacementHasInternalUpper)
                {
                    return char.ToUpperInvariant(replacement[0]) + replacement[1..];
                }
                return char.ToUpperInvariant(replacement[0]) + (replacement.Length > 1 ? replacement[1..].ToLowerInvariant() : "");
            }

            // PascalCase / Mixed starting with upper
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        if (char.IsLower(sourceSample[0]))
        {
            return char.ToLowerInvariant(replacement[0]) + replacement[1..];
        }

        return replacement;
    }

    /// <summary>
    /// Replaces occurrences of <paramref name="query"/> in <paramref name="text"/> while preserving the capitalization
    /// pattern (UPPERCASE, TitleCase, lowercase) of each matched token.
    /// </summary>
    public static (string ReplacedText, int ReplacedCount, int UpperCount, int TitleCount, int LowerCount) ReplacePreservingCase(
        string text,
        string query,
        string replacement,
        bool wholeWord = false,
        bool useRegex = false)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            return (text ?? string.Empty, 0, 0, 0, 0);

        replacement ??= string.Empty;
        var matches = FindMatches(text, query, StringComparison.OrdinalIgnoreCase, wholeWord, useRegex);
        if (matches.Count == 0)
            return (text, 0, 0, 0, 0);

        var sb = new StringBuilder(text.Length);
        int lastIdx = 0;
        int upperCount = 0;
        int titleCount = 0;
        int lowerCount = 0;

        foreach (var m in matches)
        {
            int offset = m.Index;
            int length = m.Length;
            if (offset < lastIdx || length <= 0) continue;
            sb.Append(text, lastIdx, offset - lastIdx);

            string sample = text.Substring(offset, length);
            string transformed = ApplyCasePattern(sample, replacement);
            sb.Append(transformed);

            int letters = sample.Count(char.IsLetter);
            if (letters > 1 && sample.Where(char.IsLetter).All(char.IsUpper))
                upperCount++;
            else if (letters > 0 && char.IsUpper(sample[0]))
                titleCount++;
            else
                lowerCount++;

            lastIdx = offset + length;
        }

        if (lastIdx < text.Length)
        {
            sb.Append(text, lastIdx, text.Length - lastIdx);
        }

        return (sb.ToString(), matches.Count, upperCount, titleCount, lowerCount);
    }

    /// <summary>
    /// Formats a human-readable summary of a case-preserving replacement operation.
    /// </summary>
    public static string FormatCasePreservingReplaceSummary(
        string? query,
        string? replacement,
        int replacedCount,
        int upperCount,
        int titleCount,
        int lowerCount)
    {
        string q = string.IsNullOrEmpty(query) ? "(empty)" : query;
        string r = replacement ?? string.Empty;
        if (replacedCount <= 0)
            return $"Case-preserving replace ('{q}' → '{r}'): 0 matches replaced";

        string matchLabel = replacedCount == 1 ? "1 match replaced" : $"{replacedCount} matches replaced";
        return $"Case-preserving replace ('{q}' → '{r}'): {matchLabel} ({upperCount} UPPER, {titleCount} Title, {lowerCount} lower)";
    }

    /// <summary>
    /// Automatically fixes safe, non-destructive Markdown lint issues outside fenced code blocks:
    /// - Trailing whitespace (while preserving intentional 2-space Markdown hard line breaks)
    /// - Hard tab characters (expanded to 4 spaces)
    /// - ATX headings missing a space after 1–6 leading '#' characters (e.g., "#Heading" → "# Heading")
    /// - Runs of 3+ consecutive blank lines (collapsed to at most 2 blank lines)
    /// </summary>
    public static (string FixedMarkdown, int TrailingSpacesFixed, int HardTabsFixed, int HeadingSpacesFixed, int BlankLineClustersFixed, int TotalFixes) FixSafeMarkdownLintIssues(
        string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return (markdown ?? string.Empty, 0, 0, 0, 0, 0);

        bool hasCrLf = markdown.Contains("\r\n", StringComparison.Ordinal);
        string normalized = hasCrLf ? markdown.Replace("\r\n", "\n") : markdown;
        string[] rawLines = normalized.Split('\n');

        var outputLines = new List<string>(rawLines.Length);
        char fenceChar = '\0';
        int fenceLen = 0;
        int blankRun = 0;

        int trailingFixed = 0;
        int tabsFixed = 0;
        int headingsFixed = 0;
        int blankClustersFixed = 0;

        static bool IsHardBreakLine(string line) =>
            line.EndsWith("  ", StringComparison.Ordinal) && line.Length >= 3 && !char.IsWhiteSpace(line[^3]);

        static bool IsClosingFenceLine(string trimmed, char fChar, int fLen)
        {
            int run = 0;
            while (run < trimmed.Length && trimmed[run] == fChar) run++;
            if (run < fLen) return false;
            for (int j = run; j < trimmed.Length; j++)
            {
                if (!char.IsWhiteSpace(trimmed[j])) return false;
            }
            return true;
        }

        foreach (var rawLine in rawLines)
        {
            string line = rawLine;
            string trimmedStart = line.TrimStart();

            if (fenceLen > 0)
            {
                blankRun = 0;
                if (IsClosingFenceLine(trimmedStart, fenceChar, fenceLen))
                {
                    fenceLen = 0;
                }
                outputLines.Add(line);
                continue;
            }

            if (trimmedStart.Length >= 3 && (trimmedStart[0] == '`' || trimmedStart[0] == '~'))
            {
                char ch = trimmedStart[0];
                int runLen = 0;
                while (runLen < trimmedStart.Length && trimmedStart[runLen] == ch) runLen++;
                if (runLen >= 3)
                {
                    fenceChar = ch;
                    fenceLen = runLen;
                    blankRun = 0;
                    outputLines.Add(line);
                    continue;
                }
            }

            // 1. Trailing whitespace check (preserve intentional 2-space hard breaks on non-empty lines)
            if (line.Length > 0 && char.IsWhiteSpace(line[^1]))
            {
                string trimmedEnd = line.TrimEnd();
                if (trimmedEnd.Length == 0)
                {
                    line = string.Empty;
                    trailingFixed++;
                }
                else if (!IsHardBreakLine(line))
                {
                    line = trimmedEnd;
                    trailingFixed++;
                }
            }

            // 2. Hard tabs check
            if (line.Contains('\t', StringComparison.Ordinal))
            {
                line = line.Replace("\t", "    ", StringComparison.Ordinal);
                tabsFixed++;
            }

            // Re-evaluate trimmedStart after tab expansion & trailing trim
            trimmedStart = line.TrimStart();

            // 3. Consecutive blank lines (collapse 3+ blank lines to max 2)
            if (trimmedStart.Length == 0)
            {
                blankRun++;
                if (blankRun == 3)
                {
                    blankClustersFixed++;
                }
                if (blankRun >= 3)
                {
                    continue;
                }
                outputLines.Add(string.Empty);
                continue;
            }
            else
            {
                blankRun = 0;
            }

            // 4. Missing space after ATX heading '#' (1..6 hashes followed immediately by non-space/non-#)
            if (trimmedStart[0] == '#')
            {
                int h = 0;
                while (h < trimmedStart.Length && trimmedStart[h] == '#') h++;
                if (h <= 6 && h < trimmedStart.Length && trimmedStart[h] != ' ')
                {
                    int leadingLen = line.Length - trimmedStart.Length;
                    string indent = leadingLen > 0 ? line[..leadingLen] : string.Empty;
                    line = $"{indent}{trimmedStart[..h]} {trimmedStart[h..]}";
                    headingsFixed++;
                }
            }

            outputLines.Add(line);
        }

        int totalFixes = trailingFixed + tabsFixed + headingsFixed + blankClustersFixed;
        string joined = string.Join("\n", outputLines);
        string finalMarkdown = hasCrLf ? joined.Replace("\n", "\r\n") : joined;
        return (finalMarkdown, trailingFixed, tabsFixed, headingsFixed, blankClustersFixed, totalFixes);
    }

    /// <summary>
    /// Formats a human-readable summary of safe Markdown lint auto-fixes.
    /// </summary>
    public static string FormatSafeLintFixSummary(
        int trailingSpacesFixed,
        int hardTabsFixed,
        int headingSpacesFixed,
        int blankLineClustersFixed)
    {
        int total = trailingSpacesFixed + hardTabsFixed + headingSpacesFixed + blankLineClustersFixed;
        if (total <= 0)
            return "Markdown lint auto-fix: 0 safe issues found (Document already clean)";

        string totalLabel = total == 1 ? "1 safe issue fixed" : $"{total} safe issues fixed";
        return $"Markdown lint auto-fix: {totalLabel} ({trailingSpacesFixed} trailing space, {hardTabsFixed} hard tab, {headingSpacesFixed} '#' heading space, {blankLineClustersFixed} blank-line cluster)";
    }

    private static readonly string[] CommonAiFillerPhrases =
    [
        "delve into",
        "it is important to note",
        "in conclusion",
        "testament to",
        "tapestry of",
        "furthermore,",
        "moreover,"
    ];

    private static readonly System.Text.RegularExpressions.Regex RepeatedWordRegex =
        new(@"\b([A-Za-z]{2,})\s+\1\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Audits prose text outside fenced code blocks for common cleanup and typography normalization opportunities:
    /// inline double spaces, curly smart quotes, em/en-dashes and ellipses, AI filler phrases, and repeated adjacent words.
    /// </summary>
    public static (int DoubleSpaces, int SmartQuotes, int TypographyDashesAndEllipses, int AiFillerPhrases, int RepeatedWords, int TotalOpportunities) AuditCommonProseCleanupOpportunities(
        string? text)
    {
        if (string.IsNullOrEmpty(text))
            return (0, 0, 0, 0, 0, 0);

        string normalized = text.Replace("\r\n", "\n");
        string[] lines = normalized.Split('\n');

        int doubleSpaces = 0;
        int smartQuotes = 0;
        int typographyDashes = 0;
        int aiFiller = 0;
        int repeatedWords = 0;

        char fenceChar = '\0';
        int fenceLen = 0;

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.TrimStart();
            if (fenceLen > 0)
            {
                int run = 0;
                while (run < trimmed.Length && trimmed[run] == fenceChar) run++;
                if (run >= fenceLen && trimmed[run..].All(char.IsWhiteSpace))
                {
                    fenceLen = 0;
                }
                continue;
            }

            if (trimmed.Length >= 3 && (trimmed[0] == '`' || trimmed[0] == '~'))
            {
                char ch = trimmed[0];
                int run = 0;
                while (run < trimmed.Length && trimmed[run] == ch) run++;
                if (run >= 3)
                {
                    fenceChar = ch;
                    fenceLen = run;
                    continue;
                }
            }

            string content = rawLine.Trim();
            if (content.Length == 0) continue;

            // 1. Inline double+ spaces within trimmed content
            int idx = 0;
            while (idx < content.Length - 1)
            {
                if (content[idx] == ' ' && content[idx + 1] == ' ')
                {
                    doubleSpaces++;
                    while (idx < content.Length && content[idx] == ' ') idx++;
                }
                else
                {
                    idx++;
                }
            }

            // 2. Smart quotes & 3. Typography dashes / ellipses
            foreach (char c in content)
            {
                if (c is '\u2018' or '\u2019' or '\u201C' or '\u201D')
                    smartQuotes++;
                else if (c is '\u2013' or '\u2014' or '\u2026')
                    typographyDashes++;
            }

            // 4. Common AI filler phrases
            foreach (var phrase in CommonAiFillerPhrases)
            {
                int pIdx = 0;
                while ((pIdx = content.IndexOf(phrase, pIdx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    aiFiller++;
                    pIdx += phrase.Length;
                }
            }

            // 5. Repeated adjacent words ("the the", "is is")
            repeatedWords += RepeatedWordRegex.Matches(content).Count;
        }

        int total = doubleSpaces + smartQuotes + typographyDashes + aiFiller + repeatedWords;
        return (doubleSpaces, smartQuotes, typographyDashes, aiFiller, repeatedWords, total);
    }

    /// <summary>
    /// Formats a human-readable summary of prose cleanup and typography normalization opportunities in <paramref name="text"/>.
    /// </summary>
    public static string FormatProseCleanupAuditSummary(string? text)
    {
        var (doubleSpaces, smartQuotes, typographyDashes, aiFiller, repeatedWords, _) =
            AuditCommonProseCleanupOpportunities(text);
        return FormatProseCleanupAuditSummary(doubleSpaces, smartQuotes, typographyDashes, aiFiller, repeatedWords);
    }

    /// <summary>
    /// Formats a human-readable summary of prose cleanup and typography normalization counts.
    /// </summary>
    public static string FormatProseCleanupAuditSummary(
        int doubleSpaces,
        int smartQuotes,
        int typographyDashesAndEllipses,
        int aiFillerPhrases,
        int repeatedWords)
    {
        int total = doubleSpaces + smartQuotes + typographyDashesAndEllipses + aiFillerPhrases + repeatedWords;
        if (total <= 0)
            return "Prose cleanup audit: 0 issues detected (Clean typography & prose)";

        string totalLabel = total == 1 ? "1 opportunity" : $"{total} opportunities";
        return $"Prose cleanup audit ({totalLabel}): {doubleSpaces} double-space, {smartQuotes} smart-quote, {typographyDashesAndEllipses} dash/ellipsis, {aiFillerPhrases} filler phrase, {repeatedWords} repeated word";
    }

    /// <summary>
    /// Audits the focus reading window profile and density metrics for the Looking Glass portal aperture.
    /// Returns total document words, line count, estimated words visible within the aperture,
    /// estimated reading time in aperture (seconds at 200 wpm), and total document reading time (minutes).
    /// </summary>
    public static (int TotalWords, int TotalLines, int ApertureWords, double ApertureReadSeconds, double TotalReadMinutes, string ApertureState)
        AuditFocusWindowReadingProfile(
            string? text,
            int portalRadiusPx = 45,
            bool glassBlur = false,
            bool surroundBlur = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            string emptyState = $"Scope {portalRadiusPx}% · Glass: {(glassBlur ? "On" : "Off")} · Surround: {(surroundBlur ? "On" : "Off")}";
            return (0, 0, 0, 0.0, 0.0, emptyState);
        }

        int totalLines = text.Split('\n').Length;
        var words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int totalWords = words.Length;

        // Effective reading window scale based on portal radius (0-100 range)
        int clampedScope = Math.Clamp(portalRadiusPx, 5, 100);
        // Average paragraph density: estimated words per aperture area.
        // At 45% standard spotlight, aperture spans roughly 3-5 lines of text (~18-25 words).
        int apertureWords = Math.Max(1, (int)Math.Round(clampedScope * 0.42));
        if (totalWords < apertureWords) apertureWords = totalWords;

        // At 200 words per minute = ~3.33 words/sec
        double apertureReadSeconds = Math.Round((double)apertureWords / (200.0 / 60.0), 1);
        double totalReadMinutes = Math.Round((double)totalWords / 200.0, 1);

        string blurSummary;
        if (glassBlur && surroundBlur) blurSummary = "Glass & Surround Blur Active";
        else if (glassBlur) blurSummary = "Glass Blur Active";
        else if (surroundBlur) blurSummary = "Surround Blur Active";
        else blurSummary = "Clear Preview (No Blur)";

        string state = $"Scope {clampedScope}% · {blurSummary}";
        return (totalWords, totalLines, apertureWords, apertureReadSeconds, totalReadMinutes, state);
    }

    /// <summary>
    /// Formats a human-readable focus reading window profile summary for the Looking Glass portal.
    /// </summary>
    public static string FormatFocusWindowReadingProfile(
        string? text,
        int portalRadiusPx = 45,
        bool glassBlur = false,
        bool surroundBlur = false)
    {
        var (totalWords, totalLines, apertureWords, apertureReadSec, totalReadMin, state) =
            AuditFocusWindowReadingProfile(text, portalRadiusPx, glassBlur, surroundBlur);

        if (totalWords <= 0)
        {
            return $"Focus reading profile ({state}): 0 words in document (Empty document)";
        }

        string docLabel = totalWords == 1 ? "1 word" : $"{totalWords:N0} words";
        string lineLabel = totalLines == 1 ? "1 line" : $"{totalLines:N0} lines";
        return $"Focus reading profile ({state}): ~{apertureWords} words in aperture (~{apertureReadSec:0.#}s read) · Document: {docLabel} across {lineLabel} (~{totalReadMin:0.#}m total)";
    }

    /// <summary>
    /// Represents word share, character budget, and reading time metrics for a Markdown document heading section.
    /// </summary>
    public record MarkdownSectionBudget(
        int Level,
        string Title,
        int LineNumber,
        int WordCount,
        int CharCount,
        double WordPercentage,
        double ReadingTimeMinutes);

    /// <summary>
    /// Extracts Markdown section budgets across all heading sections in <paramref name="text"/>.
    /// Ignores heading markers inside code fences (``` or ~~~ or :::).
    /// </summary>
    public static List<MarkdownSectionBudget> ExtractMarkdownSectionBudgets(string? text)
    {
        var budgets = new List<MarkdownSectionBudget>();
        if (string.IsNullOrWhiteSpace(text)) return budgets;

        // Any line break: a WinUI TextBox stores a bare '\r', which used to make one section.
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool inFence = false;
        string fenceMarker = string.Empty;

        var rawSections = new List<(int Level, string Title, int LineNumber, List<string> Lines)>();
        var currentSectionLines = new List<string>();
        int currentLevel = 0;
        string currentTitle = "Intro / Overview";
        int currentLineNumber = 1;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // Check code fences
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = tag;
                }
                else if (tag == fenceMarker || trimmed == ":::")
                {
                    inFence = false;
                    fenceMarker = string.Empty;
                }
                currentSectionLines.Add(line);
                continue;
            }

            if (!inFence && trimmed.StartsWith("#"))
            {
                int hashCount = 0;
                while (hashCount < trimmed.Length && trimmed[hashCount] == '#') hashCount++;

                if (hashCount >= 1 && hashCount <= 6 && hashCount < trimmed.Length && char.IsWhiteSpace(trimmed[hashCount]))
                {
                    // Found a heading
                    if (rawSections.Count > 0 || currentSectionLines.Any(l => !string.IsNullOrWhiteSpace(l)))
                    {
                        rawSections.Add((currentLevel, currentTitle, currentLineNumber, new List<string>(currentSectionLines)));
                        currentSectionLines.Clear();
                    }

                    currentLevel = hashCount;
                    currentTitle = trimmed.Substring(hashCount).Trim();
                    currentLineNumber = i + 1;
                    continue;
                }
            }

            currentSectionLines.Add(line);
        }

        if (currentSectionLines.Count > 0 || rawSections.Count > 0)
        {
            rawSections.Add((currentLevel, currentTitle, currentLineNumber, currentSectionLines));
        }

        // Total words in document
        var allWords = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        int totalDocWords = Math.Max(1, allWords.Length);

        foreach (var sec in rawSections)
        {
            int sectionWords = 0;
            int sectionChars = 0;
            foreach (var l in sec.Lines)
            {
                var w = l.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                sectionWords += w.Length;
                sectionChars += l.Length;
            }
            double wordPct = Math.Round((double)sectionWords / totalDocWords * 100.0, 1);
            double readMin = Math.Round((double)sectionWords / 200.0, 1);
            budgets.Add(new MarkdownSectionBudget(sec.Level, sec.Title, sec.LineNumber, sectionWords, sectionChars, wordPct, readMin));
        }

        return budgets;
    }

    /// <summary>
    /// Formats a human-readable summary of Markdown section budgets and reading density distribution.
    /// </summary>
    public static string FormatMarkdownSectionBudgetsSummary(string? text)
    {
        var budgets = ExtractMarkdownSectionBudgets(text);
        if (budgets.Count == 0)
            return "Markdown section budgets: 0 sections detected (Empty document)";

        int totalWords = budgets.Sum(b => b.WordCount);
        double totalReadMin = Math.Round((double)totalWords / 200.0, 1);
        string secLabel = budgets.Count == 1 ? "1 section" : $"{budgets.Count} sections";
        string wordLabel = totalWords == 1 ? "1 word" : $"{totalWords:N0} words";

        var sb = new StringBuilder();
        sb.AppendLine($"Markdown section budgets ({secLabel} · Total {wordLabel} · ~{totalReadMin:0.#}m read):");
        for (int i = 0; i < budgets.Count; i++)
        {
            var b = budgets[i];
            string levelTag = b.Level > 0 ? $"[H{b.Level}]" : "[Intro]";
            sb.Append($"  §{i + 1}. {levelTag} {b.Title} · {b.WordCount} words ({b.WordPercentage:0.#}%) · ~{b.ReadingTimeMinutes:0.#}m read (Line {b.LineNumber})");
            if (i < budgets.Count - 1) sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Audit report detailing footnote and reference-style link definition integrity.
    /// </summary>
    public sealed record FootnoteReferenceIntegrityAudit(
        IReadOnlyList<string> MissingFootnoteDefs,
        IReadOnlyList<string> UnusedFootnoteDefs,
        IReadOnlyList<string> DuplicateFootnoteDefs,
        IReadOnlyList<string> MissingLinkDefs,
        IReadOnlyList<string> UnusedLinkDefs,
        IReadOnlyList<string> DuplicateLinkDefs,
        int TotalFootnoteRefs,
        int TotalFootnoteDefs,
        int TotalLinkRefs,
        int TotalLinkDefs)
    {
        public bool IsClean => MissingFootnoteDefs.Count == 0 &&
                               UnusedFootnoteDefs.Count == 0 &&
                               DuplicateFootnoteDefs.Count == 0 &&
                               MissingLinkDefs.Count == 0 &&
                               UnusedLinkDefs.Count == 0 &&
                               DuplicateLinkDefs.Count == 0;
    }

    /// <summary>
    /// Audits Markdown text for missing, unused, and duplicate footnote and reference-style link definitions.
    /// Excludes fenced code and diagram blocks to prevent false positives.
    /// </summary>
    public static FootnoteReferenceIntegrityAudit AuditFootnoteAndReferenceIntegrity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new FootnoteReferenceIntegrityAudit(
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                0, 0, 0, 0);
        }

        var lines = text.Replace("\r", "").Split('\n');
        bool inFence = false;
        string fenceMarker = string.Empty;

        var footnoteDefs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicateFootnoteDefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var linkDefs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicateLinkDefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var footnoteDefRegex = new Regex(@"^[ ]{0,3}\[\^([^\]]+)\]:\s*(.*)", RegexOptions.Compiled);
        var linkDefRegex = new Regex(@"^[ ]{0,3}\[([^\]\^][^\]]*)\]:\s*(\S+)(.*)", RegexOptions.Compiled);

        var nonFenceLines = new List<(int LineNumber, string LineText, bool IsDef)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = tag;
                }
                else if (tag == fenceMarker || trimmed == ":::")
                {
                    inFence = false;
                    fenceMarker = string.Empty;
                }
                continue;
            }

            if (inFence) continue;

            // Check footnote definition
            var fnMatch = footnoteDefRegex.Match(line);
            if (fnMatch.Success)
            {
                var label = fnMatch.Groups[1].Value.Trim();
                if (footnoteDefs.ContainsKey(label))
                {
                    footnoteDefs[label]++;
                    duplicateFootnoteDefs.Add(label);
                }
                else
                {
                    footnoteDefs[label] = 1;
                }

                // Check remaining text on the definition line for other references
                var rest = fnMatch.Groups[2].Value;
                nonFenceLines.Add((i + 1, rest, true));
                continue;
            }

            // Check link definition
            var linkMatch = linkDefRegex.Match(line);
            if (linkMatch.Success)
            {
                var label = linkMatch.Groups[1].Value.Trim();
                if (linkDefs.ContainsKey(label))
                {
                    linkDefs[label]++;
                    duplicateLinkDefs.Add(label);
                }
                else
                {
                    linkDefs[label] = 1;
                }

                var rest = linkMatch.Groups[3].Value;
                nonFenceLines.Add((i + 1, rest, true));
                continue;
            }

            nonFenceLines.Add((i + 1, line, false));
        }

        var footnoteRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linkRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingFootnotes = new List<string>();
        var missingLinks = new List<string>();

        var fnRefRegex = new Regex(@"\[\^([^\]]+)\](?!:)", RegexOptions.Compiled);
        // Matches [text][ref] or [ref][]
        var fullRefRegex = new Regex(@"\[([^\]]+)\]\[([^\]]*)\]", RegexOptions.Compiled);
        // Matches shortcut reference [ref] not followed by (:
        var shortcutRefRegex = new Regex(@"(?<!\!)\[([^\]\^\r\n]+)\](?!\(|\[|:)", RegexOptions.Compiled);

        int totalFnRefs = 0;
        int totalLinkRefs = 0;

        foreach (var (_, content, _) in nonFenceLines)
        {
            if (string.IsNullOrEmpty(content)) continue;

            // Scan footnote references
            var fnMatches = fnRefRegex.Matches(content);
            foreach (Match m in fnMatches)
            {
                totalFnRefs++;
                var label = m.Groups[1].Value.Trim();
                footnoteRefs.Add(label);
                if (!footnoteDefs.ContainsKey(label) && !missingFootnotes.Contains(label, StringComparer.OrdinalIgnoreCase))
                {
                    missingFootnotes.Add(label);
                }
            }

            // Scan full reference links [text][ref] and collapsed [ref][]
            var fullMatches = fullRefRegex.Matches(content);
            foreach (Match m in fullMatches)
            {
                totalLinkRefs++;
                var textLabel = m.Groups[1].Value.Trim();
                var refLabel = m.Groups[2].Value.Trim();
                var targetKey = string.IsNullOrEmpty(refLabel) ? textLabel : refLabel;
                linkRefs.Add(targetKey);
                if (!linkDefs.ContainsKey(targetKey) && !missingLinks.Contains(targetKey, StringComparer.OrdinalIgnoreCase))
                {
                    missingLinks.Add(targetKey);
                }
            }

            // Scan shortcut links [ref] that match a known link definition
            // Mask out full reference links first so that the second bracket [ref] in [text][ref] is not double-counted
            var contentForShortcuts = fullRefRegex.Replace(content, m => new string(' ', m.Length));
            var shortcutMatches = shortcutRefRegex.Matches(contentForShortcuts);
            foreach (Match m in shortcutMatches)
            {
                var label = m.Groups[1].Value.Trim();
                if (label == " " || label == "x" || label == "X") continue; // skip checkboxes
                if (linkDefs.ContainsKey(label))
                {
                    totalLinkRefs++;
                    linkRefs.Add(label);
                }
            }
        }

        var unusedFootnotes = footnoteDefs.Keys
            .Where(k => !footnoteRefs.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unusedLinks = linkDefs.Keys
            .Where(k => !linkRefs.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FootnoteReferenceIntegrityAudit(
            missingFootnotes,
            unusedFootnotes,
            duplicateFootnoteDefs.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            missingLinks,
            unusedLinks,
            duplicateLinkDefs.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(),
            totalFnRefs,
            footnoteDefs.Count,
            totalLinkRefs,
            linkDefs.Count);
    }

    /// <summary>
    /// Formats a human-readable summary of the footnote and reference link integrity audit.
    /// </summary>
    public static string FormatFootnoteAndReferenceIntegritySummary(string? text)
    {
        var audit = AuditFootnoteAndReferenceIntegrity(text);
        if (audit.TotalFootnoteRefs == 0 && audit.TotalFootnoteDefs == 0 &&
            audit.TotalLinkRefs == 0 && audit.TotalLinkDefs == 0)
        {
            return "Footnote & reference link integrity: 0 references or definitions detected (Clean document)";
        }

        if (audit.IsClean)
        {
            return $"Footnote & reference link integrity: All {audit.TotalFootnoteDefs} footnote(s) and {audit.TotalLinkDefs} reference link(s) are valid and balanced (0 missing, 0 unused, 0 duplicate)";
        }

        var sb = new StringBuilder();
        var issuesCount = audit.MissingFootnoteDefs.Count + audit.UnusedFootnoteDefs.Count + audit.DuplicateFootnoteDefs.Count +
                          audit.MissingLinkDefs.Count + audit.UnusedLinkDefs.Count + audit.DuplicateLinkDefs.Count;
        sb.AppendLine($"Footnote & reference link integrity ({issuesCount} issue{(issuesCount == 1 ? "" : "s")} detected):");

        if (audit.MissingFootnoteDefs.Count > 0)
            sb.AppendLine($"  • Missing footnote definitions: {string.Join(", ", audit.MissingFootnoteDefs.Select(d => $"[^{d}]"))}");
        if (audit.UnusedFootnoteDefs.Count > 0)
            sb.AppendLine($"  • Unused footnote definitions: {string.Join(", ", audit.UnusedFootnoteDefs.Select(d => $"[^{d}]"))}");
        if (audit.DuplicateFootnoteDefs.Count > 0)
            sb.AppendLine($"  • Duplicate footnote definitions: {string.Join(", ", audit.DuplicateFootnoteDefs.Select(d => $"[^{d}]"))}");
        if (audit.MissingLinkDefs.Count > 0)
            sb.AppendLine($"  • Missing link definitions: {string.Join(", ", audit.MissingLinkDefs.Select(d => $"[{d}]"))}");
        if (audit.UnusedLinkDefs.Count > 0)
            sb.AppendLine($"  • Unused link definitions: {string.Join(", ", audit.UnusedLinkDefs.Select(d => $"[{d}]"))}");
        if (audit.DuplicateLinkDefs.Count > 0)
            sb.Append($"  • Duplicate link definitions: {string.Join(", ", audit.DuplicateLinkDefs.Select(d => $"[{d}]"))}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Represents the result of a writing session velocity and draft completion forecast calculation.
    /// </summary>
    public record SessionVelocityForecast(
        int CurrentWords,
        int StartWords,
        int NetWordDelta,
        double ElapsedMinutes,
        double WordsPerMinute,
        double WordsPerHour,
        string PaceCategory,
        int TargetWords,
        int WordsRemaining,
        double EstimatedMinutesRemaining,
        string Summary);

    /// <summary>
    /// Calculates word generation velocity (WPM, WPH), pacing classification, and projected time to target draft completion.
    /// </summary>
    public static SessionVelocityForecast CalculateSessionVelocityForecast(
        int currentWords,
        int startWords,
        double elapsedMinutes,
        int targetWords = 0)
    {
        int netDelta = currentWords - startWords;
        double clampedMinutes = Math.Max(0.0, elapsedMinutes);
        double wpm = 0.0;
        if (clampedMinutes > 0.05) // at least 3 seconds
        {
            wpm = Math.Round((double)netDelta / clampedMinutes, 1);
        }

        double wph = Math.Round(wpm * 60.0, 0);

        string paceCategory;
        if (netDelta < 0)
            paceCategory = "Pruning / Condensing";
        else if (Math.Abs(wpm) < 0.1)
            paceCategory = "Dormant / Idle";
        else if (wpm < 15.0)
            paceCategory = "Deliberate / Contemplative";
        else if (wpm <= 40.0)
            paceCategory = "Steady Flow";
        else if (wpm <= 80.0)
            paceCategory = "High Velocity Sprint";
        else
            paceCategory = "Hyper-Velocity Sprint";

        int wordsRemaining = 0;
        double estMinutesRemaining = 0.0;

        if (targetWords > 0)
        {
            if (currentWords >= targetWords)
            {
                wordsRemaining = 0;
                estMinutesRemaining = 0.0;
            }
            else
            {
                wordsRemaining = targetWords - currentWords;
                if (wpm > 0.1)
                {
                    estMinutesRemaining = Math.Ceiling(wordsRemaining / wpm);
                }
            }
        }

        string summary = FormatSessionVelocitySummary(
            currentWords, startWords, netDelta, clampedMinutes, wpm, wph, paceCategory, targetWords, wordsRemaining, estMinutesRemaining);

        return new SessionVelocityForecast(
            currentWords,
            startWords,
            netDelta,
            clampedMinutes,
            wpm,
            wph,
            paceCategory,
            targetWords,
            wordsRemaining,
            estMinutesRemaining,
            summary);
    }

    /// <summary>
    /// Calculates word generation velocity from markdown document text.
    /// </summary>
    public static SessionVelocityForecast CalculateSessionVelocityForecast(
        string? markdownText,
        int startWords,
        double elapsedMinutes,
        int targetWords = 0)
    {
        int currentWords = 0;
        if (!string.IsNullOrWhiteSpace(markdownText))
        {
            var words = markdownText.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            currentWords = words.Length;
        }

        return CalculateSessionVelocityForecast(currentWords, startWords, elapsedMinutes, targetWords);
    }

    /// <summary>
    /// Formats a human-readable summary of session velocity and draft completion forecast.
    /// </summary>
    public static string FormatSessionVelocitySummary(
        int currentWords,
        int startWords,
        int netDelta,
        double elapsedMinutes,
        double wpm,
        double wph,
        string paceCategory,
        int targetWords = 0,
        int wordsRemaining = 0,
        double estMinutesRemaining = 0)
    {
        if (currentWords == 0 && startWords == 0)
            return "Session velocity: 0 words · Session initialized";

        string deltaStr = netDelta >= 0 ? $"+{netDelta}" : netDelta.ToString();
        string timeStr = elapsedMinutes < 1.0 ? $"{Math.Round(elapsedMinutes * 60, 0)}s" : $"{elapsedMinutes:0.#}m";

        string targetStr = "";
        if (targetWords > 0)
        {
            if (currentWords >= targetWords)
            {
                int over = currentWords - targetWords;
                targetStr = $" · Target ({targetWords:N0}w) reached! (+{over}w over)";
            }
            else if (wpm > 0.1)
            {
                string remTime = estMinutesRemaining < 60
                    ? $"~{estMinutesRemaining:0}m remaining"
                    : $"~{Math.Floor(estMinutesRemaining / 60)}h {estMinutesRemaining % 60:0}m remaining";
                targetStr = $" · Target: {targetWords:N0}w ({wordsRemaining:N0}w left · {remTime})";
            }
            else
            {
                targetStr = $" · Target: {targetWords:N0}w ({wordsRemaining:N0}w left · Pace stalled)";
            }
        }

        return $"Session velocity ({paceCategory} · {deltaStr} words in {timeStr}): {wpm:0.#} WPM ({wph:0} WPH) · Current: {currentWords:N0} words{targetStr}";
    }

    /// <summary>
    /// Represents the result of a Flesch-Kincaid readability and prose accessibility assessment.
    /// </summary>
    public sealed record ReadabilityScorecard(
        double FleschReadingEase,
        double FleschKincaidGrade,
        string EaseCategory,
        string GradeCategory,
        int WordCount,
        int SentenceCount,
        int SyllableCount,
        int PolysyllabicWords,
        double AvgWordsPerSentence,
        double AvgSyllablesPerWord,
        string Summary);

    /// <summary>
    /// Calculates Flesch Reading Ease, Flesch-Kincaid Grade Level, sentence complexity, and syllable density from markdown text,
    /// automatically stripping code fences, inline code, and HTML tags to prevent code syntax from corrupting prose metrics.
    /// </summary>
    public static ReadabilityScorecard CalculateReadabilityScorecard(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new ReadabilityScorecard(
                0.0, 0.0, "None", "None", 0, 0, 0, 0, 0.0, 0.0,
                "Readability scorecard: 0 words · Document is empty");
        }

        // Strip code fences (```...``` or ~~~...~~~ or :::...:::)
        var clean = System.Text.RegularExpressions.Regex.Replace(
            markdownText, @"(?s)(```.*?```|~~~.*?~~~|:::.*?:::)", " ", System.Text.RegularExpressions.RegexOptions.None);

        // Strip inline code `...`
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"`[^`]*`", " ");

        // Strip HTML tags <...>
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"<[^>]+>", " ");

        // Strip Markdown heading hashes at start of lines
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?m)^\s*#{1,6}\s+", " ");

        // Extract sentences by punctuation (. ! ?) followed by whitespace or end of string
        var sentenceMatches = System.Text.RegularExpressions.Regex.Split(clean, @"(?<=[.!?])\s+");
        var sentences = sentenceMatches.Where(s => !string.IsNullOrWhiteSpace(s) && s.Any(char.IsLetter)).ToList();
        int sentenceCount = Math.Max(1, sentences.Count);

        // Extract words (letter sequences)
        var wordMatches = System.Text.RegularExpressions.Regex.Matches(clean, @"\b[a-zA-Z]+(?:'[a-zA-Z]+)?\b");
        var words = wordMatches.Select(m => m.Value).Where(w => w.Length > 0).ToList();

        if (words.Count == 0)
        {
            return new ReadabilityScorecard(
                0.0, 0.0, "None", "None", 0, 0, 0, 0, 0.0, 0.0,
                "Readability scorecard: 0 words · Document contains no readable prose");
        }

        int totalWords = words.Count;
        int totalSyllables = 0;
        int polysyllabicWords = 0;

        foreach (var w in words)
        {
            int syl = CountWordSyllables(w);
            totalSyllables += syl;
            if (syl >= 3) polysyllabicWords++;
        }

        double asl = (double)totalWords / sentenceCount;
        double asw = (double)totalSyllables / totalWords;

        // Flesch Reading Ease = 206.835 - (1.015 * ASL) - (84.6 * ASW)
        double rawEase = 206.835 - (1.015 * asl) - (84.6 * asw);
        double ease = Math.Round(Math.Clamp(rawEase, 0.0, 100.0), 1);

        // Flesch-Kincaid Grade Level = (0.39 * ASL) + (11.8 * ASW) - 15.59
        double rawGrade = (0.39 * asl) + (11.8 * asw) - 15.59;
        double grade = Math.Round(Math.Clamp(rawGrade, 0.0, 18.0), 1);

        string easeCategory = ease switch
        {
            >= 90.0 => "Very Easy (5th grade)",
            >= 80.0 => "Easy (6th grade)",
            >= 70.0 => "Fairly Easy (7th grade)",
            >= 60.0 => "Standard (8th-9th grade)",
            >= 50.0 => "Fairly Difficult (10th-12th grade)",
            >= 30.0 => "Difficult (College)",
            _ => "Very Difficult (Academic/Specialized)"
        };

        string gradeCategory = grade switch
        {
            <= 6.0 => "Elementary",
            <= 8.0 => "Middle School",
            <= 12.0 => "High School",
            <= 16.0 => "Undergraduate",
            _ => "Graduate / Specialized"
        };

        double avgWords = Math.Round(asl, 1);
        double avgSyllables = Math.Round(asw, 2);

        string summary = $"Readability scorecard (Score: {ease:0.#}/100 · {easeCategory}): Grade {grade:0.#} ({gradeCategory}) · {totalWords:N0}w, {sentenceCount:N0}s ({avgWords:0.#} wps, {avgSyllables:0.##} spw · {polysyllabicWords} polysyllabic)";

        return new ReadabilityScorecard(
            ease, grade, easeCategory, gradeCategory, totalWords, sentenceCount, totalSyllables, polysyllabicWords, avgWords, avgSyllables, summary);
    }

    /// <summary>
    /// Counts phonetic syllables in an English word using vowel cluster heuristics.
    /// </summary>
    private static int CountWordSyllables(string word)
    {
        if (string.IsNullOrEmpty(word)) return 0;
        string w = word.ToLowerInvariant();
        if (w.Length <= 3) return 1;

        int count = 0;
        bool prevVowel = false;
        for (int i = 0; i < w.Length; i++)
        {
            bool isVowel = "aeiouy".IndexOf(w[i]) >= 0;
            if (isVowel && !prevVowel)
            {
                count++;
            }
            prevVowel = isVowel;
        }

        // Silent trailing 'e' (except when ending in 'le' preceded by consonant)
        if (w.EndsWith("e") && !w.EndsWith("le") && count > 1)
        {
            count--;
        }

        return Math.Max(1, count);
    }

    /// <summary>
    /// Formats a human-readable summary of Flesch-Kincaid readability and prose accessibility metrics.
    /// </summary>
    public static string FormatReadabilityScorecardSummary(string? markdownText)
    {
        return CalculateReadabilityScorecard(markdownText).Summary;
    }

    /// <summary>
    /// Represents the result of a sentence flow, paragraph cadence, and prose rhythm analysis.
    /// </summary>
    public sealed record SentenceFlowCadence(
        int ParagraphCount,
        int SentenceCount,
        int WordCount,
        double AvgWordsPerSentence,
        int MaxSentenceWords,
        int RunOnCount,
        int MicroSentenceCount,
        string CadenceRating,
        string Summary);

    /// <summary>
    /// Audits markdown prose for paragraph length, sentence flow cadence, run-on sentences (>35 words),
    /// and micro-sentences (≤3 words), automatically stripping code blocks, HTML, and tables.
    /// </summary>
    public static SentenceFlowCadence AuditSentenceFlowCadence(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new SentenceFlowCadence(
                0, 0, 0, 0.0, 0, 0, 0, "None",
                "Sentence flow cadence: 0 words · Document is empty");
        }

        // Strip code fences (```...``` or ~~~...~~~ or :::...:::)
        var clean = System.Text.RegularExpressions.Regex.Replace(
            markdownText, @"(?s)(```.*?```|~~~.*?~~~|:::.*?:::)", " ", System.Text.RegularExpressions.RegexOptions.None);

        // Strip inline code `...`
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"`[^`]*`", " ");

        // Strip HTML tags <...>
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"<[^>]+>", " ");

        // Strip image embeds ![alt](url)
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"!\[.*?\]\(.*?\)", " ");

        // Strip Markdown headings at start of lines
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"(?m)^\s*#{1,6}\s+", " ");

        // Split into paragraphs by blank lines
        var rawParagraphs = System.Text.RegularExpressions.Regex.Split(clean, @"\r?\n\s*\r?\n");
        var paragraphs = rawParagraphs.Where(p => !string.IsNullOrWhiteSpace(p) && p.Any(char.IsLetter)).ToList();

        if (paragraphs.Count == 0)
        {
            return new SentenceFlowCadence(
                0, 0, 0, 0.0, 0, 0, 0, "None",
                "Sentence flow cadence: 0 words · Document contains no readable prose paragraphs");
        }

        int totalSentences = 0;
        int totalWords = 0;
        int maxSentenceWords = 0;
        int runOnCount = 0;
        int microCount = 0;

        foreach (var p in paragraphs)
        {
            var rawSentences = System.Text.RegularExpressions.Regex.Split(p.Trim(), @"(?<=[.!?])\s+");
            var sentences = rawSentences.Where(s => !string.IsNullOrWhiteSpace(s) && s.Any(char.IsLetter)).ToList();
            if (sentences.Count == 0) continue;

            foreach (var s in sentences)
            {
                var wordMatches = System.Text.RegularExpressions.Regex.Matches(s, @"\b[a-zA-Z]+(?:'[a-zA-Z]+)?\b");
                int wordsInSentence = wordMatches.Count;
                if (wordsInSentence == 0) continue;

                totalSentences++;
                totalWords += wordsInSentence;
                if (wordsInSentence > maxSentenceWords) maxSentenceWords = wordsInSentence;

                if (wordsInSentence > 35) runOnCount++;
                else if (wordsInSentence <= 3) microCount++;
            }
        }

        if (totalSentences == 0 || totalWords == 0)
        {
            return new SentenceFlowCadence(
                paragraphs.Count, 0, 0, 0.0, 0, 0, 0, "None",
                "Sentence flow cadence: 0 words · Document contains no complete sentences");
        }

        double avgWords = Math.Round((double)totalWords / totalSentences, 1);

        string rating;
        if (runOnCount > 2 || (runOnCount > 0 && runOnCount >= totalSentences * 0.25))
            rating = "Run-On Heavy (Needs Pacing Breaks)";
        else if (avgWords < 8.0)
            rating = "Staccato (Short Punchy Sentences)";
        else if (avgWords > 28.0)
            rating = "Dense / Academic (High Cognitive Load)";
        else if (maxSentenceWords - avgWords > 15.0)
            rating = "Dynamic Flow (High Rhythm Variation)";
        else
            rating = "Balanced Flow";

        string summary = $"Sentence flow cadence ({rating} · {avgWords:0.#} wps): {paragraphs.Count:N0} paragraphs, {totalSentences:N0} sentences ({totalWords:N0} words) · {runOnCount} run-on (>35w), {microCount} micro (≤3w) · Max: {maxSentenceWords}w";

        return new SentenceFlowCadence(
            paragraphs.Count,
            totalSentences,
            totalWords,
            avgWords,
            maxSentenceWords,
            runOnCount,
            microCount,
            rating,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of sentence flow cadence and paragraph rhythm.
    /// </summary>
    public static string FormatSentenceFlowSummary(string? markdownText)
    {
        return AuditSentenceFlowCadence(markdownText).Summary;
    }

    /// <summary>
    /// Audits markdown text for Git merge conflict markers and common syntax ambiguities.
    /// </summary>
    public static ConflictMarkerAndAmbiguityAudit AuditConflictMarkersAndAmbiguities(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new ConflictMarkerAndAmbiguityAudit(
                0, 0, Array.Empty<string>(), Array.Empty<string>(), true,
                "Conflict markers & syntax ambiguities: Clean (0 issues detected)");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var conflictLines = new List<string>();
        var ambiguities = new List<string>();

        bool inFence = false;
        int mathDisplayDelimiterCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                // Check conflict markers
                if (trimmed.StartsWith("<<<<<<<") ||
                    trimmed.StartsWith("=======") ||
                    trimmed.StartsWith(">>>>>>>") ||
                    trimmed.StartsWith("|||||||"))
                {
                    conflictLines.Add($"Line {i + 1}: {trimmed}");
                }

                // Check unclosed inline backticks (odd number of backticks in a single non-fence line)
                int backtickCount = 0;
                for (int b = 0; b < raw.Length; b++)
                {
                    if (raw[b] == '`') backtickCount++;
                }
                if (backtickCount % 2 != 0)
                {
                    ambiguities.Add($"Line {i + 1}: Unclosed inline code backtick (odd count: {backtickCount})");
                }

                // Count $$ display math occurrences
                int idx = 0;
                while ((idx = raw.IndexOf("$$", idx, StringComparison.Ordinal)) != -1)
                {
                    mathDisplayDelimiterCount++;
                    idx += 2;
                }
            }
        }

        if (mathDisplayDelimiterCount % 2 != 0)
        {
            ambiguities.Add($"Document has unmatched display math delimiter ($$ count: {mathDisplayDelimiterCount})");
        }

        bool isClean = conflictLines.Count == 0 && ambiguities.Count == 0;
        string summary;
        if (isClean)
        {
            summary = "✓ Conflict markers & syntax ambiguities: Clean (0 issues detected)";
        }
        else
        {
            var parts = new List<string>();
            if (conflictLines.Count > 0)
            {
                string cLabel = conflictLines.Count == 1 ? "1 conflict marker" : $"{conflictLines.Count} conflict markers";
                parts.Add(cLabel);
            }
            if (ambiguities.Count > 0)
            {
                string aLabel = ambiguities.Count == 1 ? "1 syntax ambiguity" : $"{ambiguities.Count} syntax ambiguities";
                parts.Add(aLabel);
            }

            string preview = conflictLines.Count > 0 ? conflictLines[0] : ambiguities[0];
            summary = $"⚠ {string.Join(" and ", parts)} detected · {preview}";
        }

        return new ConflictMarkerAndAmbiguityAudit(
            conflictLines.Count,
            ambiguities.Count,
            conflictLines,
            ambiguities,
            isClean,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of merge conflict markers and syntax ambiguities.
    /// </summary>
    public static string FormatConflictMarkerSummary(string? markdownText)
    {
        return AuditConflictMarkersAndAmbiguities(markdownText).Summary;
    }

    /// <summary>
    /// Audits comprehensive multi-formula readability metrics across Markdown prose, including
    /// Flesch Reading Ease, Flesch-Kincaid Grade Level, Gunning Fog Index, Coleman-Liau Index,
    /// Automated Readability Index (ARI), and estimated reading/speaking times.
    /// Code fences, display math blocks, HTML elements, and tables are stripped prior to calculation.
    /// </summary>
    public static ReadabilityMetricsAudit AuditReadabilityMetrics(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new ReadabilityMetricsAudit(
                0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
                "None", "None", 0, 0, 0, 0, 0,
                TimeSpan.Zero, TimeSpan.Zero,
                "Readability metrics: 0 words · Document contains no readable prose");
        }

        // 1. Strip non-prose Markdown constructs
        string prose = markdownText;
        prose = System.Text.RegularExpressions.Regex.Replace(prose, @"```[\s\S]*?```", " ", System.Text.RegularExpressions.RegexOptions.Multiline);
        prose = System.Text.RegularExpressions.Regex.Replace(prose, @":::[\s\S]*?:::", " ", System.Text.RegularExpressions.RegexOptions.Multiline);
        prose = System.Text.RegularExpressions.Regex.Replace(prose, @"\$\$[\s\S]*?\$\$", " ", System.Text.RegularExpressions.RegexOptions.Multiline);
        prose = System.Text.RegularExpressions.Regex.Replace(prose, @"`[^`]*`", " ");
        prose = System.Text.RegularExpressions.Regex.Replace(prose, @"<[^>]+>", " ");
        prose = System.Text.RegularExpressions.Regex.Replace(prose, @"!\[.*?\]\(.*?\)", " ");
        prose = System.Text.RegularExpressions.Regex.Replace(prose, @"^\[.*?\]:\s*\S+.*$", " ", System.Text.RegularExpressions.RegexOptions.Multiline);
        prose = System.Text.RegularExpressions.Regex.Replace(prose, @"^\s*\|.*?\|\s*$", " ", System.Text.RegularExpressions.RegexOptions.Multiline);

        // 2. Count sentences
        var sentenceDelimiters = new[] { '.', '!', '?' };
        var rawSentences = prose.Split(sentenceDelimiters, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => s.Length > 0 && s.Any(char.IsLetter))
                                .ToList();
        int sentenceCount = Math.Max(1, rawSentences.Count);

        // 3. Extract words
        var wordMatches = System.Text.RegularExpressions.Regex.Matches(prose, @"\b[A-Za-z]+(?:'[A-Za-z]+)?\b");
        var words = wordMatches.Select(m => m.Value).Where(w => w.Length > 0).ToList();

        if (words.Count == 0)
        {
            return new ReadabilityMetricsAudit(
                0.0, 0.0, 0.0, 0.0, 0.0, 0.0,
                "None", "None", 0, 0, 0, 0, 0,
                TimeSpan.Zero, TimeSpan.Zero,
                "Readability metrics: 0 words · Document contains no readable prose");
        }

        int totalWords = words.Count;
        int totalSyllables = 0;
        int complexWords = 0;
        int totalLetters = 0;
        int totalCharacters = 0;

        foreach (var w in words)
        {
            int syl = CountWordSyllables(w);
            totalSyllables += syl;
            if (syl >= 3) complexWords++;
            totalLetters += w.Count(char.IsLetter);
            totalCharacters += w.Length;
        }

        double asl = (double)totalWords / sentenceCount;
        double asw = (double)totalSyllables / totalWords;

        // Flesch Reading Ease = 206.835 - (1.015 * ASL) - (84.6 * ASW)
        double rawEase = 206.835 - (1.015 * asl) - (84.6 * asw);
        double fleschEase = Math.Round(Math.Clamp(rawEase, 0.0, 100.0), 1);

        // Flesch-Kincaid Grade Level = (0.39 * ASL) + (11.8 * ASW) - 15.59
        double rawFkGrade = (0.39 * asl) + (11.8 * asw) - 15.59;
        double fkGrade = Math.Round(Math.Clamp(rawFkGrade, 0.0, 18.0), 1);

        // Gunning Fog Index = 0.4 * (ASL + 100 * (ComplexWords / Words))
        double rawGunning = 0.4 * (asl + 100.0 * ((double)complexWords / totalWords));
        double gunningFog = Math.Round(Math.Clamp(rawGunning, 0.0, 20.0), 1);

        // Coleman-Liau Index = 0.0588 * L - 0.296 * S - 15.8 (L = letters/100w, S = sentences/100w)
        double lVal = 100.0 * ((double)totalLetters / totalWords);
        double sVal = 100.0 * ((double)sentenceCount / totalWords);
        double rawCli = (0.0588 * lVal) - (0.296 * sVal) - 15.8;
        double cli = Math.Round(Math.Clamp(rawCli, 0.0, 18.0), 1);

        // Automated Readability Index (ARI) = 4.71 * (Characters / Words) + 0.5 * ASL - 21.43
        double rawAri = 4.71 * ((double)totalCharacters / totalWords) + (0.5 * asl) - 21.43;
        double ari = Math.Round(Math.Clamp(rawAri, 0.0, 18.0), 1);

        // Consensus grade
        double consensus = Math.Round(Math.Clamp((fkGrade + gunningFog + cli + ari) / 4.0, 1.0, 18.0), 1);

        string easeCategory = fleschEase switch
        {
            >= 90.0 => "Very Easy",
            >= 80.0 => "Easy",
            >= 70.0 => "Fairly Easy",
            >= 60.0 => "Standard",
            >= 50.0 => "Fairly Difficult",
            >= 30.0 => "Difficult",
            _ => "Very Difficult"
        };

        string audienceCategory = consensus switch
        {
            <= 6.0 => "Elementary (Grades 1-5)",
            <= 8.0 => "Middle School (Grades 6-8)",
            <= 12.0 => "High School (Grades 9-12)",
            <= 16.0 => "Undergraduate (College)",
            _ => "Specialized / Post-Graduate"
        };

        var readTime = TimeSpan.FromMinutes((double)totalWords / 200.0);
        var speakTime = TimeSpan.FromMinutes((double)totalWords / 130.0);

        string readTimeStr = readTime.TotalMinutes < 1.0 ? $"{Math.Max(1, (int)readTime.TotalSeconds)}s" : $"{readTime.TotalMinutes:0.#}m";
        string speakTimeStr = speakTime.TotalMinutes < 1.0 ? $"{Math.Max(1, (int)speakTime.TotalSeconds)}s" : $"{speakTime.TotalMinutes:0.#}m";

        string summary = $"Readability audit: Grade {consensus:0.#} ({audienceCategory}) · Ease: {fleschEase:0.#}/100 ({easeCategory}) · Fog: {gunningFog:0.#} · CLI: {cli:0.#} · ARI: {ari:0.#} · {totalWords:N0}w ({readTimeStr} read, {speakTimeStr} speak)";

        return new ReadabilityMetricsAudit(
            fleschEase, fkGrade, gunningFog, cli, ari, consensus,
            easeCategory, audienceCategory,
            totalWords, sentenceCount, totalSyllables, complexWords, totalCharacters,
            readTime, speakTime, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of comprehensive readability metrics.
    /// </summary>
    public static string FormatReadabilitySummary(string? markdownText)
    {
        return AuditReadabilityMetrics(markdownText).Summary;
    }

    /// <summary>
    /// Formats a human-readable summary of heading outline structural balance and depth distribution for the given document text.
    /// </summary>
    public static string FormatHeadingOutlineBalanceSummary(string? markdownText)
    {
        return AuditHeadingOutlineBalance(markdownText).Summary;
    }

    /// <summary>
    /// Audits markdown heading outline structure, depth distribution, and detects skipped hierarchy levels.
    /// </summary>
    public static HeadingOutlineBalanceAudit AuditHeadingOutlineBalance(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new HeadingOutlineBalanceAudit(
                0, 0, 0, 0,
                new Dictionary<int, int> { { 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 }, { 6, 0 } },
                Array.Empty<string>(),
                Array.Empty<HeadingOutlineItem>(),
                "No Headings (Unstructured)",
                "Outline balance audit: 0 headings (Empty document) · No outline structure detected");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inFence = false;
        string fenceMarker = string.Empty;

        var headings = new List<HeadingOutlineItem>();
        var levelCounts = new Dictionary<int, int>
        {
            { 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 }, { 6, 0 }
        };
        var skippedWarnings = new List<string>();

        int prevLevel = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = tag;
                }
                else if (tag == fenceMarker || trimmed == ":::")
                {
                    inFence = false;
                    fenceMarker = string.Empty;
                }
                continue;
            }

            if (inFence) continue;

            // Check ATX headings: # to ######
            if (trimmed.StartsWith('#'))
            {
                int level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;

                if (level >= 1 && level <= 6 && (level == trimmed.Length || char.IsWhiteSpace(trimmed[level])))
                {
                    string title = trimmed.Substring(level).Trim();
                    levelCounts[level]++;

                    if (prevLevel > 0 && level > prevLevel + 1)
                    {
                        skippedWarnings.Add($"Line {i + 1}: Skipped level (H{prevLevel} -> H{level}) at '{title}'");
                    }
                    else if (prevLevel == 0 && level > 2)
                    {
                        skippedWarnings.Add($"Line {i + 1}: Document starts at deep level H{level} without H1/H2 context at '{title}'");
                    }

                    headings.Add(new HeadingOutlineItem(i + 1, level, title));
                    prevLevel = level;
                }
            }
        }

        if (headings.Count == 0)
        {
            return new HeadingOutlineBalanceAudit(
                0, 0, 0, 0,
                levelCounts,
                Array.Empty<string>(),
                Array.Empty<HeadingOutlineItem>(),
                "No Headings (Unstructured)",
                "Outline balance audit: 0 headings · Document contains no markdown headings");
        }

        int totalHeadings = headings.Count;
        int maxDepth = headings.Max(h => h.Level);
        int minDepth = headings.Min(h => h.Level);
        int skippedCount = skippedWarnings.Count;

        string balanceCategory;
        if (skippedCount > 0)
        {
            balanceCategory = "Fragmented Outline (Skipped Levels)";
        }
        else if (maxDepth == 1 && levelCounts[1] > 1)
        {
            balanceCategory = "Flat Outline (Single Tier)";
        }
        else if (levelCounts[1] == 1 && maxDepth <= 4 && skippedCount == 0)
        {
            balanceCategory = "Well-Balanced Outline";
        }
        else if (maxDepth >= 5)
        {
            balanceCategory = "Deep Hierarchy (H5/H6)";
        }
        else
        {
            balanceCategory = "Multi-Tier Hierarchy";
        }

        string summary = FormatHeadingOutlineBalanceSummary(totalHeadings, minDepth, maxDepth, skippedCount, balanceCategory, levelCounts);

        return new HeadingOutlineBalanceAudit(
            totalHeadings, minDepth, maxDepth, skippedCount,
            levelCounts, skippedWarnings, headings,
            balanceCategory, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of heading outline structural balance and depth distribution.
    /// </summary>
    public static string FormatHeadingOutlineBalanceSummary(
        int totalHeadings, int minDepth, int maxDepth, int skippedLevels, string balanceCategory, IReadOnlyDictionary<int, int>? levelCounts = null)
    {
        if (totalHeadings <= 0) return "Outline balance audit: 0 headings · Document contains no markdown headings";

        string hLabel = totalHeadings == 1 ? "1 heading" : $"{totalHeadings} headings";
        string range = minDepth == maxDepth ? $"H{minDepth}" : $"H{minDepth}–H{maxDepth}";

        string dist = "";
        if (levelCounts != null)
        {
            var active = levelCounts.Where(kvp => kvp.Value > 0).Select(kvp => $"{kvp.Value} H{kvp.Key}");
            dist = $" [{string.Join(", ", active)}]";
        }

        string skippedStr = skippedLevels > 0 ? $" · {skippedLevels} skipped levels warning" : " · No skipped levels";
        return $"Outline balance audit ({hLabel} across {range}{dist} · {balanceCategory}){skippedStr}";
    }

    /// <summary>
    /// Audits code fence language coverage, tagged vs untagged code blocks, and inline code spans across markdown text.
    /// </summary>
    public static CodeFenceLanguageCoverageAudit AuditCodeFenceLanguageCoverage(string? markdownText)
    {
        var languageCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new CodeFenceLanguageCoverageAudit(
                0, 0, 0, 0, 0, 100.0,
                languageCounts,
                "No Code Blocks",
                "Code fence language coverage: 0 code blocks · Document contains no code blocks");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inFence = false;
        string fenceMarker = "";
        int totalFenced = 0;
        int taggedFenced = 0;
        int untaggedFenced = 0;
        int totalCodeLines = 0;
        int inlineSpans = 0;

        foreach (var rawLine in lines)
        {
            var trimmed = rawLine.TrimStart();

            if (!inFence)
            {
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = true;
                    fenceMarker = trimmed.Substring(0, 3);
                    totalFenced++;

                    // Extract language tag
                    string info = trimmed.Substring(3).Trim();
                    string lang = "";
                    if (!string.IsNullOrEmpty(info))
                    {
                        var parts = info.Split(new[] { ' ', '\t', '{' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            lang = parts[0].Trim().TrimStart('.').ToLowerInvariant();
                        }
                    }

                    if (!string.IsNullOrEmpty(lang))
                    {
                        taggedFenced++;
                        languageCounts[lang] = languageCounts.TryGetValue(lang, out int count) ? count + 1 : 1;
                    }
                    else
                    {
                        untaggedFenced++;
                    }
                }
                else
                {
                    int idx = 0;
                    while (idx < rawLine.Length)
                    {
                        int start = rawLine.IndexOf('`', idx);
                        if (start < 0) break;
                        if (start + 1 < rawLine.Length && rawLine[start + 1] == '`')
                        {
                            idx = start + 2;
                            while (idx < rawLine.Length && rawLine[idx] == '`') idx++;
                            continue;
                        }
                        int end = rawLine.IndexOf('`', start + 1);
                        if (end < 0) break;
                        if (end > start + 1)
                        {
                            inlineSpans++;
                        }
                        idx = end + 1;
                    }
                }
            }
            else
            {
                if (trimmed.StartsWith(fenceMarker))
                {
                    inFence = false;
                }
                else
                {
                    totalCodeLines++;
                }
            }
        }

        double coveragePercent = totalFenced > 0
            ? Math.Round((double)taggedFenced * 100.0 / totalFenced, 1)
            : 100.0;

        string category;
        if (totalFenced == 0 && inlineSpans == 0)
        {
            category = "No Code Blocks";
        }
        else if (totalFenced == 0 && inlineSpans > 0)
        {
            category = "Inline Code Only";
        }
        else if (untaggedFenced == 0)
        {
            category = "Fully Tagged (100%)";
        }
        else if (coveragePercent >= 80.0)
        {
            category = "High Coverage (≥80%)";
        }
        else if (coveragePercent >= 50.0)
        {
            category = "Mixed Coverage (50–79%)";
        }
        else
        {
            category = "Untagged Heavy (<50%)";
        }

        string summary = FormatCodeFenceLanguageCoverageSummary(
            totalFenced, taggedFenced, untaggedFenced, inlineSpans, totalCodeLines, coveragePercent, category, languageCounts);

        return new CodeFenceLanguageCoverageAudit(
            totalFenced, taggedFenced, untaggedFenced, inlineSpans, totalCodeLines,
            coveragePercent, languageCounts, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of code fence language coverage and untagged block detection from markdown.
    /// </summary>
    public static string FormatCodeFenceLanguageCoverageSummary(string? markdownText)
        => AuditCodeFenceLanguageCoverage(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of code fence language coverage metrics.
    /// </summary>
    public static string FormatCodeFenceLanguageCoverageSummary(
        int totalFenced, int taggedFenced, int untaggedFenced, int inlineSpans, int totalCodeLines,
        double coveragePercent, string category, IReadOnlyDictionary<string, int>? languages = null)
    {
        if (totalFenced == 0 && inlineSpans == 0)
            return "Code fence language coverage: 0 code blocks · Document contains no code blocks";

        if (totalFenced == 0)
            return $"Code fence language coverage (0 fenced, {inlineSpans} inline code spans · {category}): No fenced blocks to audit";

        string fencedLabel = totalFenced == 1 ? "1 fenced block" : $"{totalFenced} fenced blocks";
        string tagBreakdown = $"{taggedFenced} tagged, {untaggedFenced} untagged";
        string inlineLabel = inlineSpans == 1 ? "1 inline span" : $"{inlineSpans} inline spans";

        string langSummary = "";
        if (languages != null && languages.Count > 0)
        {
            var topLangs = languages.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} ({kv.Value})");
            langSummary = $" · Top: {string.Join(", ", topLangs)}";
        }

        return $"Code fence language coverage ({fencedLabel} [{tagBreakdown}], {inlineLabel} · {category}): {coveragePercent:0.#}% tagged ({totalCodeLines} code lines){langSummary}";
    }

    /// <summary>
    /// Audits markdown list item hierarchy, nesting depth, and indentation consistency.
    /// Evaluates ordered vs unordered lists, indentation jumps, and structural balance with code fence immunity.
    /// </summary>
    public static ListIndentationAudit AuditListIndentationHierarchy(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new ListIndentationAudit(
                0, 0, 0, 0, 0.0, 0, 0, "No Lists",
                "List indentation hierarchy: 0 list items · Document contains no lists");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inFence = false;
        string fenceMarker = "";

        int totalItems = 0;
        int orderedCount = 0;
        int unorderedCount = 0;
        int maxDepth = 0;
        int totalIndentSpaces = 0;
        int inconsistentJumps = 0;
        int mixedTypeJumps = 0;

        int prevIndent = -1;
        bool prevWasOrdered = false;

        var listRegex = new System.Text.RegularExpressions.Regex(@"^(\s*)([-*+]|\d+\.)\s+(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();

            // Code fence handling
            if (!inFence)
            {
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = true;
                    fenceMarker = trimmed.Substring(0, 3);
                    continue;
                }
            }
            else
            {
                if (trimmed.StartsWith(fenceMarker))
                {
                    inFence = false;
                }
                continue;
            }

            // Strip leading blockquote markers
            string cleanLine = rawLine;
            while (cleanLine.TrimStart().StartsWith(">"))
            {
                int gtIdx = cleanLine.IndexOf('>');
                cleanLine = cleanLine.Substring(gtIdx + 1);
            }

            var match = listRegex.Match(cleanLine);
            if (match.Success)
            {
                totalItems++;
                string indentStr = match.Groups[1].Value;
                string marker = match.Groups[2].Value;

                int indentSpaces = 0;
                foreach (char c in indentStr)
                {
                    if (c == '\t') indentSpaces += 4;
                    else indentSpaces++;
                }

                totalIndentSpaces += indentSpaces;
                bool isOrdered = char.IsDigit(marker[0]);
                if (isOrdered) orderedCount++;
                else unorderedCount++;

                int depth = indentSpaces >= 8 ? (indentSpaces / 4) : (indentSpaces / 2);
                if (depth > maxDepth) maxDepth = depth;

                if (prevIndent >= 0)
                {
                    if (indentSpaces > prevIndent + 4)
                    {
                        inconsistentJumps++;
                    }

                    if (indentSpaces > prevIndent && isOrdered != prevWasOrdered)
                    {
                        mixedTypeJumps++;
                    }
                }

                prevIndent = indentSpaces;
                prevWasOrdered = isOrdered;
            }
            else if (string.IsNullOrWhiteSpace(cleanLine))
            {
                prevIndent = -1;
            }
        }

        if (totalItems == 0)
        {
            return new ListIndentationAudit(
                0, 0, 0, 0, 0.0, 0, 0, "No Lists",
                "List indentation hierarchy: 0 list items · Document contains no lists");
        }

        double avgIndent = Math.Round((double)totalIndentSpaces / totalItems, 1);

        string category;
        if (inconsistentJumps > 0)
        {
            category = "Inconsistent Indentation";
        }
        else if (maxDepth == 0)
        {
            category = "Flat List (Single Level)";
        }
        else if (mixedTypeJumps > 0)
        {
            category = "Mixed Ordered/Unordered Hierarchy";
        }
        else if (maxDepth >= 4)
        {
            category = "Deep Hierarchy (≥4 Levels)";
        }
        else
        {
            category = "Clean Multi-Tier Hierarchy";
        }

        string summary = FormatListIndentationSummary(
            totalItems, orderedCount, unorderedCount, maxDepth, avgIndent, inconsistentJumps, mixedTypeJumps, category);

        return new ListIndentationAudit(
            totalItems, orderedCount, unorderedCount, maxDepth, avgIndent, inconsistentJumps, mixedTypeJumps, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown list indentation hierarchy.
    /// </summary>
    public static string FormatListIndentationSummary(string? markdownText)
        => AuditListIndentationHierarchy(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of list indentation metrics.
    /// </summary>
    public static string FormatListIndentationSummary(
        int totalItems, int orderedCount, int unorderedCount, int maxDepth, double avgIndent,
        int inconsistentJumps, int mixedTypeJumps, string category)
    {
        if (totalItems == 0)
            return "List indentation hierarchy: 0 list items · Document contains no lists";

        string itemsLabel = totalItems == 1 ? "1 item" : $"{totalItems} items";
        string breakdown = $"{unorderedCount} unordered, {orderedCount} ordered";
        string depthLabel = maxDepth == 0 ? "flat" : $"max depth {maxDepth}";
        string issueNote = inconsistentJumps > 0 ? $" · ⚠ {inconsistentJumps} indent jumps" : "";
        string mixedNote = mixedTypeJumps > 0 ? $" · {mixedTypeJumps} mixed transitions" : "";

        return $"List hierarchy ({itemsLabel} [{breakdown}], {depthLabel} · {category}): avg indent {avgIndent:0.#}sp{issueNote}{mixedNote}";
    }

    private static List<string> SplitMarkdownTableCells(string line)
    {
        var cells = new List<string>();
        string trimmed = line.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|") && !trimmed.EndsWith(@"\|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

        var currentCell = new StringBuilder();
        bool inCodeSpan = false;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '`')
            {
                inCodeSpan = !inCodeSpan;
                currentCell.Append(c);
            }
            else if (c == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                currentCell.Append('|');
                i++;
            }
            else if (c == '|' && !inCodeSpan)
            {
                cells.Add(currentCell.ToString().Trim());
                currentCell.Clear();
            }
            else
            {
                currentCell.Append(c);
            }
        }
        cells.Add(currentCell.ToString().Trim());
        return cells;
    }

    private static bool IsTableDelimiterRow(List<string> cells)
    {
        if (cells.Count == 0) return false;
        foreach (var c in cells)
        {
            string t = c.Trim();
            if (string.IsNullOrEmpty(t)) return false;
            if (!Regex.IsMatch(t, @"^:?-+:?$")) return false;
        }
        return true;
    }

    /// <summary>
    /// Audits markdown tables for pipe grid consistency, expected column counts, and ragged rows.
    /// </summary>
    public static TableColumnAlignmentAudit AuditMarkdownTableColumnAlignment(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new TableColumnAlignmentAudit(
                0, 0, 0, 0, Array.Empty<MarkdownTableInfo>(), "No Tables",
                "Markdown tables: 0 tables · Document contains no markdown tables");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var tables = new List<MarkdownTableInfo>();

        bool inCodeFence = false;
        var currentTableLines = new List<(int LineNumber, string Line)>();

        void ProcessCurrentTable()
        {
            if (currentTableLines.Count < 2)
            {
                currentTableLines.Clear();
                return;
            }

            var parsedRows = currentTableLines.Select(tl => (tl.LineNumber, Cells: SplitMarkdownTableCells(tl.Line))).ToList();
            if (parsedRows.Count < 2)
            {
                currentTableLines.Clear();
                return;
            }

            int delimiterIndex = -1;
            for (int r = 1; r < Math.Min(3, parsedRows.Count); r++)
            {
                if (IsTableDelimiterRow(parsedRows[r].Cells))
                {
                    delimiterIndex = r;
                    break;
                }
            }

            if (delimiterIndex < 1)
            {
                // Not a valid markdown table without a delimiter separator
                currentTableLines.Clear();
                return;
            }

            int expectedCols = parsedRows[delimiterIndex].Cells.Count;
            var alignments = new List<string>();
            foreach (var delCell in parsedRows[delimiterIndex].Cells)
            {
                string dt = delCell.Trim();
                bool left = dt.StartsWith(":");
                bool right = dt.EndsWith(":");
                if (left && right) alignments.Add("center");
                else if (left) alignments.Add("left");
                else if (right) alignments.Add("right");
                else alignments.Add("default");
            }

            int raggedCount = 0;
            int totalRows = 0;

            for (int r = 0; r < parsedRows.Count; r++)
            {
                if (r == delimiterIndex) continue; // Skip separator line from row count
                totalRows++;
                if (parsedRows[r].Cells.Count != expectedCols)
                {
                    raggedCount++;
                }
            }

            int startLine = currentTableLines[0].LineNumber;
            int endLine = currentTableLines[^1].LineNumber;

            tables.Add(new MarkdownTableInfo(
                startLine, endLine, expectedCols, totalRows, raggedCount, true, alignments));

            currentTableLines.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                if (currentTableLines.Count > 0)
                {
                    ProcessCurrentTable();
                }
                continue;
            }

            if (inCodeFence) continue;

            if (trimmed.Contains("|") && !string.IsNullOrWhiteSpace(trimmed))
            {
                currentTableLines.Add((i + 1, line));
            }
            else
            {
                if (currentTableLines.Count > 0)
                {
                    ProcessCurrentTable();
                }
            }
        }

        if (currentTableLines.Count > 0)
        {
            ProcessCurrentTable();
        }

        if (tables.Count == 0)
        {
            return new TableColumnAlignmentAudit(
                0, 0, 0, 0, Array.Empty<MarkdownTableInfo>(), "No Tables",
                "Markdown tables: 0 tables · Document contains no markdown tables");
        }

        int totalTables = tables.Count;
        int totalTableRows = tables.Sum(t => t.TotalRows);
        int totalRaggedRows = tables.Sum(t => t.RaggedRowCount);
        int maxCols = tables.Max(t => t.ExpectedColumnCount);

        string category = totalRaggedRows == 0 ? "Perfect Grid Alignment" : "Ragged Rows Detected";
        string summary = FormatTableAlignmentSummary(totalTables, totalTableRows, totalRaggedRows, maxCols, category);

        return new TableColumnAlignmentAudit(
            totalTables, totalTableRows, totalRaggedRows, maxCols, tables, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown table column alignment.
    /// </summary>
    public static string FormatTableAlignmentSummary(string? markdownText)
        => AuditMarkdownTableColumnAlignment(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of table alignment metrics.
    /// </summary>
    public static string FormatTableAlignmentSummary(
        int totalTables, int totalRows, int raggedRows, int maxCols, string category)
    {
        if (totalTables == 0)
            return "Markdown tables: 0 tables · Document contains no markdown tables";

        string tableLabel = totalTables == 1 ? "1 table" : $"{totalTables} tables";
        string rowLabel = totalRows == 1 ? "1 row" : $"{totalRows} rows";
        string colLabel = maxCols == 1 ? "1 col" : $"max {maxCols} cols";
        string raggedNote = raggedRows > 0 ? $" · ⚠ {raggedRows} ragged {(raggedRows == 1 ? "row" : "rows")}" : "";

        return $"Markdown tables ({tableLabel} [{rowLabel}, {colLabel}] · {category}){raggedNote}";
    }

    /// <summary>
    /// Audits markdown task lists, checkboxes (- [ ], - [x], etc.), completion velocity,
    /// and section-level progress across the document.
    /// </summary>
    public static TaskListProgressAudit AuditTaskListProgress(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new TaskListProgressAudit(
                0, 0, 0, 0.0, Array.Empty<TaskListSectionProgress>(), Array.Empty<TaskListItem>(),
                "No Tasks", "Markdown tasks: 0 items · Document contains no checklist items");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        bool inCodeFence = false;
        bool inHtmlComment = false;
        string currentSection = "General";

        var allTasks = new List<TaskListItem>();
        var taskRegex = new Regex(@"^\s*(?:[-*+]|\d+\.)\s+\[([ xX])\]\s*(.*)$", RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            if (trimmed.StartsWith("<!--") && !trimmed.EndsWith("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;

            if (trimmed.StartsWith("#"))
            {
                int hIndex = 0;
                while (hIndex < trimmed.Length && trimmed[hIndex] == '#') hIndex++;
                currentSection = trimmed[hIndex..].Trim();
                if (string.IsNullOrWhiteSpace(currentSection)) currentSection = "General";
                continue;
            }

            var match = taskRegex.Match(line);
            if (match.Success)
            {
                char check = match.Groups[1].Value[0];
                bool isCompleted = check == 'x' || check == 'X';
                string text = match.Groups[2].Value.Trim();
                allTasks.Add(new TaskListItem(i + 1, currentSection, text, isCompleted));
            }
        }

        if (allTasks.Count == 0)
        {
            return new TaskListProgressAudit(
                0, 0, 0, 0.0, Array.Empty<TaskListSectionProgress>(), Array.Empty<TaskListItem>(),
                "No Tasks", "Markdown tasks: 0 items · Document contains no checklist items");
        }

        int totalTasks = allTasks.Count;
        int completedTasks = allTasks.Count(t => t.IsCompleted);
        int pendingTasks = totalTasks - completedTasks;
        double completionPercentage = Math.Round((double)completedTasks / totalTasks * 100.0, 1);

        var sectionGroups = allTasks.GroupBy(t => t.Section, StringComparer.OrdinalIgnoreCase);
        var sectionProgressList = new List<TaskListSectionProgress>();
        foreach (var group in sectionGroups)
        {
            int sTotal = group.Count();
            int sCompleted = group.Count(t => t.IsCompleted);
            int sPending = sTotal - sCompleted;
            double sPct = Math.Round((double)sCompleted / sTotal * 100.0, 1);
            sectionProgressList.Add(new TaskListSectionProgress(group.Key, sTotal, sCompleted, sPending, sPct));
        }

        var pendingSample = allTasks.Where(t => !t.IsCompleted).Take(3).ToList();

        string category;
        if (completedTasks == totalTasks)
        {
            category = "All Tasks Completed (100%)";
        }
        else if (completionPercentage >= 75.0)
        {
            category = "High Velocity (Near Complete)";
        }
        else if (completionPercentage >= 40.0)
        {
            category = "Active In-Progress";
        }
        else if (completionPercentage > 0.0)
        {
            category = "Early Execution Phase";
        }
        else
        {
            category = "All Pending (Not Started)";
        }

        string summary = FormatTaskListProgressSummary(
            totalTasks, completedTasks, pendingTasks, completionPercentage, category, sectionProgressList, pendingSample);

        return new TaskListProgressAudit(
            totalTasks, completedTasks, pendingTasks, completionPercentage,
            sectionProgressList, pendingSample, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown task list progress.
    /// </summary>
    public static string FormatTaskListProgressSummary(string? markdownText)
        => AuditTaskListProgress(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of task list progress metrics.
    /// </summary>
    public static string FormatTaskListProgressSummary(
        int totalTasks, int completedTasks, int pendingTasks, double completionPercentage,
        string category, IReadOnlyList<TaskListSectionProgress>? sectionProgress = null,
        IReadOnlyList<TaskListItem>? samplePending = null)
    {
        if (totalTasks == 0)
            return "Markdown tasks: 0 items · Document contains no checklist items";

        string itemLabel = totalTasks == 1 ? "1 task" : $"{totalTasks} tasks";
        string completedLabel = completedTasks == 1 ? "1 completed" : $"{completedTasks} completed";

        string nextUp = "";
        if (samplePending != null && samplePending.Count > 0)
        {
            var first = samplePending[0];
            string shortTask = first.TaskText.Length > 40 ? first.TaskText[..37] + "..." : first.TaskText;
            nextUp = $" · Next: [L{first.LineNumber}] {shortTask}";
        }

        return $"Markdown tasks ({itemLabel} · {completedLabel} ({completionPercentage:0.#}%), {pendingTasks} pending · {category}){nextUp}";
    }

    /// <summary>
    /// Audits markdown footnotes for bidirectional linkage between inline references ([^id])
    /// and footnote definitions ([^id]: ...), detecting missing targets, orphans, and duplicate definitions.
    /// </summary>
    public static FootnoteLinkageAudit AuditFootnoteLinkage(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new FootnoteLinkageAudit(
                0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<FootnoteSequenceItem>(), Array.Empty<FootnoteDefinitionItem>(),
                "No Footnotes", "Markdown footnotes: 0 items · Document contains no footnotes");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        bool inCodeFence = false;
        bool inHtmlComment = false;

        var references = new List<FootnoteSequenceItem>();
        var definitions = new List<FootnoteDefinitionItem>();

        var defRegex = new Regex(@"^\s*\[\^([a-zA-Z0-9_\-]+)\]:\s*(.*)$", RegexOptions.Compiled);
        var refRegex = new Regex(@"\[\^([a-zA-Z0-9_\-]+)\](?!:)", RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            if (trimmed.StartsWith("<!--") && !trimmed.EndsWith("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;

            var defMatch = defRegex.Match(line);
            if (defMatch.Success)
            {
                string id = defMatch.Groups[1].Value;
                string preview = defMatch.Groups[2].Value.Trim();
                definitions.Add(new FootnoteDefinitionItem(i + 1, id, preview));
                string bodyPart = defMatch.Groups[2].Value;
                var inlineMatches = refRegex.Matches(bodyPart);
                foreach (Match m in inlineMatches)
                {
                    references.Add(new FootnoteSequenceItem(i + 1, m.Groups[1].Value));
                }
            }
            else
            {
                var matches = refRegex.Matches(line);
                foreach (Match m in matches)
                {
                    references.Add(new FootnoteSequenceItem(i + 1, m.Groups[1].Value));
                }
            }
        }

        if (references.Count == 0 && definitions.Count == 0)
        {
            return new FootnoteLinkageAudit(
                0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<FootnoteSequenceItem>(), Array.Empty<FootnoteDefinitionItem>(),
                "No Footnotes", "Markdown footnotes: 0 items · Document contains no footnotes");
        }

        var referencedIds = references.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var definedIdGroups = definitions.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var definedIds = definedIdGroups.Select(g => g.Key).ToList();

        var definedSet = new HashSet<string>(definedIds, StringComparer.OrdinalIgnoreCase);
        var referencedSet = new HashSet<string>(referencedIds, StringComparer.OrdinalIgnoreCase);

        var unresolvedIds = referencedIds.Where(id => !definedSet.Contains(id)).ToList();
        var orphanIds = definedIds.Where(id => !referencedSet.Contains(id)).ToList();
        var duplicateDefs = definedIdGroups.Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        string category;
        if (unresolvedIds.Count == 0 && orphanIds.Count == 0 && duplicateDefs.Count == 0)
        {
            category = "100% Bidirectional Linkage";
        }
        else if (unresolvedIds.Count > 0 && orphanIds.Count > 0)
        {
            category = "Broken Footnote Linkage";
        }
        else if (unresolvedIds.Count > 0)
        {
            category = "Missing Footnote Definitions";
        }
        else if (orphanIds.Count > 0)
        {
            category = "Orphan Footnote Definitions";
        }
        else
        {
            category = "Duplicate Footnote Definitions";
        }

        string summary = FormatFootnoteLinkageSummary(
            references.Count, referencedIds.Count, definitions.Count, definedIds.Count,
            unresolvedIds, orphanIds, duplicateDefs, category, references, definitions);

        return new FootnoteLinkageAudit(
            references.Count, referencedIds.Count, definitions.Count, definedIds.Count,
            unresolvedIds, orphanIds, duplicateDefs, references, definitions, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown footnote reference and definition linkage.
    /// </summary>
    public static string FormatFootnoteLinkageSummary(string? markdownText)
        => AuditFootnoteLinkage(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of footnote linkage metrics.
    /// </summary>
    public static string FormatFootnoteLinkageSummary(
        int totalReferences, int uniqueReferencedIds, int totalDefinitions, int uniqueDefinedIds,
        IReadOnlyList<string> unresolvedIds, IReadOnlyList<string> orphanIds, IReadOnlyList<string> duplicateDefs,
        string category, IReadOnlyList<FootnoteSequenceItem>? references = null,
        IReadOnlyList<FootnoteDefinitionItem>? definitions = null)
    {
        if (totalReferences == 0 && totalDefinitions == 0)
            return "Markdown footnotes: 0 items · Document contains no footnotes";

        string refLabel = totalReferences == 1 ? "1 ref" : $"{totalReferences} refs";
        string defLabel = totalDefinitions == 1 ? "1 def" : $"{totalDefinitions} defs";

        string issues = "";
        if (unresolvedIds.Count > 0)
        {
            issues += $" · {unresolvedIds.Count} missing defs [^{string.Join(", [^", unresolvedIds.Take(2))}]";
        }
        if (orphanIds.Count > 0)
        {
            issues += $" · {orphanIds.Count} orphan defs [^{string.Join(", [^", orphanIds.Take(2))}]";
        }
        if (duplicateDefs.Count > 0)
        {
            issues += $" · {duplicateDefs.Count} duplicate defs [^{string.Join(", [^", duplicateDefs.Take(2))}]";
        }

        return $"Markdown footnotes ({refLabel}, {defLabel} · {category}){issues}";
    }

    /// <summary>
    /// Audits markdown YAML frontmatter for schema compliance, required key presence,
    /// duplicate keys, and unexpected/custom properties.
    /// </summary>
    public static FrontmatterSchemaAudit AuditFrontmatterYamlSchema(
        string? markdownText,
        IEnumerable<string>? requiredKeys = null,
        IEnumerable<string>? standardKeys = null)
    {
        var reqList = requiredKeys?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim().ToLowerInvariant()).Distinct().ToList()
            ?? new List<string> { "title", "author", "date" };

        var stdSet = new HashSet<string>(
            standardKeys?.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim().ToLowerInvariant())
            ?? new[] { "title", "author", "date", "description", "version", "tags", "category", "draft", "slug", "layout" },
            StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "YAML frontmatter schema: 0 properties · Document is empty";
            return new FrontmatterSchemaAudit(
                false, false, 0, Array.Empty<string>(), reqList, Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<FrontmatterPropertyItem>(), "Empty Document", 0.0, emptySummary);
        }

        string text = markdownText;
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        var lines = text.Replace("\r", "").Split('\n');
        int firstNonEmpty = 0;
        while (firstNonEmpty < lines.Length && string.IsNullOrWhiteSpace(lines[firstNonEmpty]))
            firstNonEmpty++;

        if (firstNonEmpty >= lines.Length || !lines[firstNonEmpty].Trim().StartsWith("---"))
        {
            string noFrontmatterSummary = "YAML frontmatter schema: No Frontmatter · Document contains no YAML header";
            return new FrontmatterSchemaAudit(
                false, false, 0, Array.Empty<string>(), reqList, Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<FrontmatterPropertyItem>(), "No Frontmatter", 0.0, noFrontmatterSummary);
        }

        bool isClosed = false;
        int closingLineIndex = -1;
        for (int i = firstNonEmpty + 1; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed == "---" || trimmed == "...")
            {
                isClosed = true;
                closingLineIndex = i;
                break;
            }
        }

        int maxLine = isClosed ? closingLineIndex : lines.Length;
        var properties = new List<FrontmatterPropertyItem>();
        var seenKeys = new List<string>();
        var duplicateKeys = new List<string>();
        var keyRegex = new Regex(@"^([a-zA-Z0-9_\-]+)\s*:\s*(.*)$", RegexOptions.Compiled);

        for (int i = firstNonEmpty + 1; i < maxLine; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#")) continue;

            var match = keyRegex.Match(trimmed);
            if (match.Success)
            {
                string key = match.Groups[1].Value.ToLowerInvariant();
                string rawVal = match.Groups[2].Value.Trim();

                if (seenKeys.Contains(key) && !duplicateKeys.Contains(key))
                {
                    duplicateKeys.Add(key);
                }
                seenKeys.Add(key);

                string inferredType;
                if (rawVal.StartsWith("[") && rawVal.EndsWith("]")) inferredType = "List";
                else if (rawVal.Equals("true", StringComparison.OrdinalIgnoreCase) || rawVal.Equals("false", StringComparison.OrdinalIgnoreCase)) inferredType = "Boolean";
                else if (double.TryParse(rawVal, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)) inferredType = "Number";
                else if (Regex.IsMatch(rawVal, @"^\d{4}-\d{2}-\d{2}")) inferredType = "Date";
                else inferredType = "String";

                properties.Add(new FrontmatterPropertyItem(i + 1, key, rawVal, inferredType));
            }
        }

        var presentDistinctKeys = properties.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var presentSet = new HashSet<string>(presentDistinctKeys, StringComparer.OrdinalIgnoreCase);

        var missingRequired = reqList.Where(k => !presentSet.Contains(k)).ToList();
        var customKeys = presentDistinctKeys.Where(k => !stdSet.Contains(k)).ToList();

        double completeness = reqList.Count > 0
            ? Math.Round(((reqList.Count - missingRequired.Count) / (double)reqList.Count) * 100.0, 1)
            : 100.0;

        string category;
        if (!isClosed)
        {
            category = "Unclosed Frontmatter";
        }
        else if (duplicateKeys.Count > 0)
        {
            category = "Duplicate Keys Detected";
        }
        else if (missingRequired.Count > 0)
        {
            category = "Missing Required Keys";
        }
        else if (customKeys.Count > 0)
        {
            category = "Valid Schema (Extended Properties)";
        }
        else
        {
            category = "Valid Complete Schema";
        }

        string summary = FormatFrontmatterSchemaSummary(
            true, isClosed, properties.Count, presentDistinctKeys, missingRequired,
            customKeys, duplicateKeys, category, completeness);

        return new FrontmatterSchemaAudit(
            true, isClosed, properties.Count, presentDistinctKeys, missingRequired,
            customKeys, duplicateKeys, properties, category, completeness, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of YAML frontmatter metadata and schema validity.
    /// </summary>
    public static string FormatFrontmatterSchemaSummary(string? markdownText, IEnumerable<string>? requiredKeys = null)
        => AuditFrontmatterYamlSchema(markdownText, requiredKeys).Summary;

    public static string FormatFrontmatterSchemaSummary(
        bool hasFrontmatter, bool isClosed, int propCount,
        IReadOnlyList<string> presentKeys, IReadOnlyList<string> missingRequired,
        IReadOnlyList<string> customKeys, IReadOnlyList<string> duplicateKeys,
        string category, double completeness)
    {
        if (!hasFrontmatter)
            return "YAML frontmatter schema: No Frontmatter · Document contains no YAML header";

        string propLabel = propCount == 1 ? "1 property" : $"{propCount} properties";
        string issues = "";
        if (!isClosed) issues += " · Unclosed YAML block (missing closing '---')";
        if (missingRequired.Count > 0) issues += $" · Missing: {string.Join(", ", missingRequired)}";
        if (duplicateKeys.Count > 0) issues += $" · Duplicates: {string.Join(", ", duplicateKeys)}";
        if (customKeys.Count > 0) issues += $" · {customKeys.Count} custom: {string.Join(", ", customKeys.Take(2))}";

        return $"YAML frontmatter schema ({propLabel} · {category} · {completeness:0.#}% complete){issues}";
    }

    /// <summary>
    /// Audits document Table of Contents (TOC) depth hierarchy consistency, heading anchor slug collisions,
    /// and internal markdown anchor link validity (#anchor).
    /// </summary>
    public static TocAnchorAudit AuditTocDepthAndAnchorCollisions(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new TocAnchorAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                Array.Empty<HeadingAnchorItem>(),
                Array.Empty<TocLinkItem>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                "Empty Document", 100.0,
                "TOC & anchor audit: 0 headings · Document contains no headings or anchor links");
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        bool inCodeBlock = false;
        var headings = new List<HeadingAnchorItem>();
        var slugOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var definedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicateSlugs = new List<string>();

        int prevLevel = 0;
        int hierarchyJumps = 0;

        // Pass 1: Parse headings and compute GitHub-style anchor slugs
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock) continue;

            // Match ATX heading (# to ######)
            var headingMatch = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(#{1,6})\s+(.+)$");
            if (headingMatch.Success)
            {
                int level = headingMatch.Groups[1].Length;
                string rawTitle = headingMatch.Groups[2].Value.TrimEnd('#').Trim();

                // Strip markdown formatting for slug generation
                string cleanTitle = System.Text.RegularExpressions.Regex.Replace(rawTitle, @"\[(.*?)\]\(.*?\)", "$1");
                cleanTitle = System.Text.RegularExpressions.Regex.Replace(cleanTitle, @"[*_`~]", "");

                // GitHub slugify: lowercase, strip punctuation, replace whitespace with hyphens
                string baseSlug = System.Text.RegularExpressions.Regex.Replace(cleanTitle.ToLowerInvariant(), @"[^\w\s-]", "");
                baseSlug = System.Text.RegularExpressions.Regex.Replace(baseSlug, @"\s+", "-").Trim('-');
                if (string.IsNullOrEmpty(baseSlug)) baseSlug = "section";

                string finalSlug;
                if (!slugOccurrences.TryGetValue(baseSlug, out int count))
                {
                    slugOccurrences[baseSlug] = 1;
                    finalSlug = baseSlug;
                }
                else
                {
                    slugOccurrences[baseSlug] = count + 1;
                    finalSlug = $"{baseSlug}-{count}";
                    if (count == 1) duplicateSlugs.Add(baseSlug);
                }

                definedSlugs.Add(finalSlug);

                bool hasJump = false;
                int jumpFrom = prevLevel;
                if (prevLevel > 0 && level > prevLevel + 1)
                {
                    hasJump = true;
                    hierarchyJumps++;
                }

                prevLevel = level;
                headings.Add(new HeadingAnchorItem(i + 1, level, rawTitle, finalSlug, hasJump, jumpFrom));
            }
        }

        // Pass 2: Parse internal anchor links [text](#slug)
        inCodeBlock = false;
        var anchorLinks = new List<TocLinkItem>();
        var brokenSlugs = new List<string>();
        int selfReferentialCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock) continue;

            var linkMatches = System.Text.RegularExpressions.Regex.Matches(line, @"\[([^\]]+)\]\(#([a-zA-Z0-9_\-]+)\)");
            foreach (System.Text.RegularExpressions.Match match in linkMatches)
            {
                string linkText = match.Groups[1].Value;
                string targetSlug = match.Groups[2].Value;

                bool isValid = definedSlugs.Contains(targetSlug);
                if (!isValid && !brokenSlugs.Contains(targetSlug, StringComparer.OrdinalIgnoreCase))
                {
                    brokenSlugs.Add(targetSlug);
                }

                // Check self-referential (heading linking to itself)
                bool isSelfRef = false;
                var currentHeading = headings.FirstOrDefault(h => h.LineNumber == i + 1);
                if (currentHeading != null && string.Equals(currentHeading.Slug, targetSlug, StringComparison.OrdinalIgnoreCase))
                {
                    isSelfRef = true;
                    selfReferentialCount++;
                }

                anchorLinks.Add(new TocLinkItem(i + 1, linkText, targetSlug, isValid, isSelfRef));
            }
        }

        int totalHeadings = headings.Count;
        int minLevel = totalHeadings > 0 ? headings.Min(h => h.Level) : 0;
        int maxLevel = totalHeadings > 0 ? headings.Max(h => h.Level) : 0;
        int totalLinks = anchorLinks.Count;
        int validLinks = anchorLinks.Count(l => l.IsValidHeadingTarget);
        int brokenLinks = anchorLinks.Count(l => !l.IsValidHeadingTarget);

        double integrity;
        if (totalHeadings == 0 && totalLinks == 0)
        {
            integrity = 100.0;
        }
        else if (totalLinks == 0)
        {
            integrity = Math.Max(0.0, 100.0 - (hierarchyJumps * 15.0) - (duplicateSlugs.Count * 10.0));
        }
        else
        {
            double linkRatio = (double)validLinks / totalLinks * 100.0;
            integrity = Math.Max(0.0, linkRatio - (hierarchyJumps * 10.0) - (duplicateSlugs.Count * 5.0));
        }
        integrity = Math.Round(Math.Clamp(integrity, 0.0, 100.0), 1);

        string category;
        if (totalHeadings == 0)
        {
            category = "No Headings Found";
        }
        else if (brokenLinks > 0)
        {
            category = "Broken Anchor References Detected";
        }
        else if (hierarchyJumps > 0)
        {
            category = "Skipped Depth Levels Detected";
        }
        else if (duplicateSlugs.Count > 0)
        {
            category = "Heading Anchor Slug Collisions";
        }
        else
        {
            category = "Consistent Hierarchy (100% Valid)";
        }

        string summary = FormatTocAnchorSummary(
            totalHeadings, minLevel, maxLevel, hierarchyJumps, totalLinks, validLinks,
            brokenLinks, selfReferentialCount, duplicateSlugs.Count, category, integrity, brokenSlugs);

        return new TocAnchorAudit(
            totalHeadings, minLevel, maxLevel, hierarchyJumps, totalLinks, validLinks,
            brokenLinks, selfReferentialCount, duplicateSlugs.Count,
            headings, anchorLinks, brokenSlugs, duplicateSlugs,
            category, integrity, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of document TOC depth and heading anchor validity.
    /// </summary>
    public static string FormatTocAnchorSummary(string? markdownText)
        => AuditTocDepthAndAnchorCollisions(markdownText).Summary;

    public static string FormatTocAnchorSummary(
        int totalHeadings, int minLevel, int maxLevel, int hierarchyJumps,
        int totalLinks, int validLinks, int brokenLinks, int selfRefLinks,
        int duplicateSlugs, string category, double integrity, IReadOnlyList<string> brokenSlugs)
    {
        if (totalHeadings == 0 && totalLinks == 0)
            return "TOC & anchor audit: 0 headings · Document contains no headings or anchor links";

        string headingDesc = totalHeadings == 1 ? "1 heading" : $"{totalHeadings} headings";
        string depthSpan = totalHeadings > 0 ? $" (H{minLevel}-H{maxLevel})" : "";
        string linkDesc = totalLinks > 0 ? $", {totalLinks} anchor links ({validLinks} valid, {brokenLinks} broken)" : "";
        string issues = "";

        if (hierarchyJumps > 0) issues += $" · ⚠ {hierarchyJumps} skipped levels";
        if (brokenLinks > 0) issues += $" · ⚠ {brokenLinks} broken targets ({string.Join(", ", brokenSlugs.Take(2))})";
        if (duplicateSlugs > 0) issues += $" · {duplicateSlugs} duplicate slugs";
        if (selfRefLinks > 0) issues += $" · {selfRefLinks} self-referential";

        return $"TOC & anchor audit: {headingDesc}{depthSpan}{linkDesc} · {category} · {integrity:0.#}% integrity{issues}";
    }

    /// <summary>
    /// Audits CommonMark reference-style link definitions ([label]: url) and inline reference links
    /// ([text][label], [label][], [label]), identifying missing target definitions, orphan definitions,
    /// duplicate label definitions, and overall linkage integrity.
    /// </summary>
    public static ReferenceLinkIntegrityAudit AuditReferenceLinkIntegrity(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new ReferenceLinkIntegrityAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ReferenceDefinitionItem>(),
                Array.Empty<ReferenceLinkItem>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                "Empty Document",
                "Reference link audit: 0 links · Document contains no reference link definitions or usages");
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        bool inCodeBlock = false;
        bool inHtmlComment = false;

        var definitions = new List<ReferenceDefinitionItem>();
        var definedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labelCountMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicateLabels = new List<string>();

        // Regex for reference link definition: ^[ ]{0,3}\[([^\]\^]+)\]:\s*(\S+)(?:\s+(?:\"([^\"]*)\"|'([^']*)'|\(([^)]*)\)))?\s*$
        var defRegex = new System.Text.RegularExpressions.Regex(
            @"^[ ]{0,3}\[([^\]\^]+)\]:\s*(\S+)(?:\s+(?:\""([^\""]*)\""|'([^']*)'|\(([^)]*)\)))?\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        // Pass 1: Collect reference definitions
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }
            if (inCodeBlock) continue;

            if (trimmed.StartsWith("<!--") && !trimmed.EndsWith("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;

            var match = defRegex.Match(line);
            if (match.Success)
            {
                string rawLabel = match.Groups[1].Value;
                string cleanLabel = rawLabel.Trim();
                string destination = match.Groups[2].Value;
                string title = match.Groups[3].Success ? match.Groups[3].Value
                             : match.Groups[4].Success ? match.Groups[4].Value
                             : match.Groups[5].Success ? match.Groups[5].Value : "";

                if (!labelCountMap.TryGetValue(cleanLabel, out int c))
                {
                    labelCountMap[cleanLabel] = 1;
                    definedLabels.Add(cleanLabel);
                }
                else
                {
                    labelCountMap[cleanLabel] = c + 1;
                    if (c == 1) duplicateLabels.Add(cleanLabel);
                }

                definitions.Add(new ReferenceDefinitionItem(i + 1, cleanLabel, destination, title));
            }
        }

        // Pass 2: Collect reference link usages
        inCodeBlock = false;
        inHtmlComment = false;
        var links = new List<ReferenceLinkItem>();
        var brokenLabels = new List<string>();
        var referencedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var explicitRefRegex = new System.Text.RegularExpressions.Regex(
            @"\[([^\]\^]+)\]\[([^\]]*)\]",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var shortcutRefRegex = new System.Text.RegularExpressions.Regex(
            @"(?<!!\s*)\[([^\]\^]+)\](?![\[\(:])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }
            if (inCodeBlock) continue;

            if (trimmed.StartsWith("<!--") && !trimmed.EndsWith("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;

            if (defRegex.IsMatch(line)) continue;

            string lineNoCode = System.Text.RegularExpressions.Regex.Replace(line, @"`[^`]+`", "");

            var explicitMatches = explicitRefRegex.Matches(lineNoCode);
            var matchedSpans = new List<(int Start, int Length)>();

            foreach (System.Text.RegularExpressions.Match m in explicitMatches)
            {
                string text = m.Groups[1].Value.Trim();
                string label = m.Groups[2].Value.Trim();
                string effectiveLabel = string.IsNullOrEmpty(label) ? text : label;
                string refType = string.IsNullOrEmpty(label) ? "Collapsed" : "Full";

                bool isResolved = definedLabels.Contains(effectiveLabel);
                if (isResolved)
                {
                    referencedLabels.Add(effectiveLabel);
                }
                else if (!brokenLabels.Contains(effectiveLabel, StringComparer.OrdinalIgnoreCase))
                {
                    brokenLabels.Add(effectiveLabel);
                }

                links.Add(new ReferenceLinkItem(i + 1, text, effectiveLabel, refType, isResolved));
                matchedSpans.Add((m.Index, m.Length));
            }

            if (definedLabels.Count > 0)
            {
                var shortcutMatches = shortcutRefRegex.Matches(lineNoCode);
                foreach (System.Text.RegularExpressions.Match m in shortcutMatches)
                {
                    bool overlaps = matchedSpans.Any(s => m.Index >= s.Start && m.Index < s.Start + s.Length);
                    if (overlaps) continue;

                    string candidate = m.Groups[1].Value.Trim();
                    if (definedLabels.Contains(candidate))
                    {
                        referencedLabels.Add(candidate);
                        links.Add(new ReferenceLinkItem(i + 1, candidate, candidate, "Shortcut", true));
                    }
                }
            }
        }

        var orphanLabels = definedLabels.Where(l => !referencedLabels.Contains(l)).ToList();

        int totalDefs = definitions.Count;
        int uniqueDefs = definedLabels.Count;
        int totalLinks = links.Count;
        int resolvedLinks = links.Count(l => l.IsResolved);
        int brokenLinks = links.Count(l => !l.IsResolved);
        int orphanCount = orphanLabels.Count;
        int duplicateDefCount = duplicateLabels.Count;

        double integrityScore;
        if (totalDefs == 0 && totalLinks == 0)
        {
            integrityScore = 100.0;
        }
        else
        {
            double linkScore = totalLinks > 0 ? ((double)resolvedLinks / totalLinks) * 70.0 : 70.0;
            double defScore = totalDefs > 0 ? (1.0 - ((double)orphanCount / totalDefs)) * 20.0 : 20.0;
            double dupPenalty = duplicateDefCount * 5.0;
            integrityScore = Math.Round(Math.Clamp(linkScore + defScore + 10.0 - dupPenalty, 0.0, 100.0), 1);
        }

        string category;
        if (totalDefs == 0 && totalLinks == 0)
        {
            category = "No Reference Links";
        }
        else if (brokenLinks > 0 && orphanCount > 0)
        {
            category = "Broken Links & Orphan Definitions";
        }
        else if (brokenLinks > 0)
        {
            category = "Broken Reference Links Detected";
        }
        else if (orphanCount > 0)
        {
            category = "Orphan Reference Definitions Present";
        }
        else if (duplicateDefCount > 0)
        {
            category = "Duplicate Reference Definitions";
        }
        else
        {
            category = "Optimal Reference Linkage (100%)";
        }

        string summary = FormatReferenceLinkIntegritySummary(
            totalDefs, uniqueDefs, totalLinks, resolvedLinks, brokenLinks,
            orphanCount, duplicateDefCount, integrityScore, category, brokenLabels, orphanLabels);

        return new ReferenceLinkIntegrityAudit(
            totalDefs, uniqueDefs, totalLinks, resolvedLinks, brokenLinks,
            orphanCount, duplicateDefCount, integrityScore,
            definitions, links, brokenLabels, orphanLabels, duplicateLabels,
            category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of CommonMark reference-style link definitions and linkage integrity.
    /// </summary>
    public static string FormatReferenceLinkIntegritySummary(string? markdownText)
        => AuditReferenceLinkIntegrity(markdownText).Summary;

    public static string FormatReferenceLinkIntegritySummary(
        int totalDefs, int uniqueDefs, int totalLinks, int resolvedLinks, int brokenLinks,
        int orphanDefs, int duplicateDefs, double integrityScore, string category,
        IReadOnlyList<string> brokenLabels, IReadOnlyList<string> orphanLabels)
    {
        if (totalDefs == 0 && totalLinks == 0)
            return "Reference link audit: 0 links · Document contains no reference link definitions or usages";

        string defDesc = totalDefs == 1 ? "1 definition" : $"{totalDefs} definitions";
        string linkDesc = totalLinks == 1 ? "1 reference link" : $"{totalLinks} reference links";
        string breakdown = $"{resolvedLinks} resolved, {brokenLinks} broken, {orphanDefs} orphans";

        string issues = "";
        if (brokenLinks > 0) issues += $" · ⚠ {brokenLinks} broken targets ({string.Join(", ", brokenLabels.Take(2))})";
        if (orphanDefs > 0) issues += $" · ⚠ {orphanDefs} orphan defs ({string.Join(", ", orphanLabels.Take(2))})";
        if (duplicateDefs > 0) issues += $" · {duplicateDefs} duplicate labels";

        return $"Reference link audit: {defDesc}, {linkDesc} [{breakdown}] · {category} · {integrityScore:0.#}% integrity{issues}";
    }

    /// <summary>
    /// Audits markdown text for syntax ambiguity, unclosed delimiter pairs (inline code, math, brackets, emphasis),
    /// nested syntax collisions, and generates auto-repair preview proposals.
    /// </summary>
    public static SyntaxAmbiguityAudit AuditSyntaxAmbiguityAndDelimiterBalance(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Syntax ambiguity audit: 0 issues · Document is empty";
            return new SyntaxAmbiguityAudit(0, 0, 0, 0, 0, 100.0, Array.Empty<DelimiterBalanceItem>(), "Empty Document", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var ambiguities = new List<DelimiterBalanceItem>();

        bool inCodeFence = false;
        bool inHtmlComment = false;
        bool inMathBlock = false;

        int unclosedCode = 0;
        int unclosedEmphasis = 0;
        int unclosedMath = 0;
        int unmatchedBrackets = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Code fence handling
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            // HTML comments
            if (trimmed.StartsWith("<!--") && !trimmed.EndsWith("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;

            // Multiline Math Block ($$)
            if (trimmed.StartsWith("$$") && trimmed.Length == 2)
            {
                inMathBlock = !inMathBlock;
                continue;
            }
            if (inMathBlock)
            {
                if (trimmed.EndsWith("$$")) inMathBlock = false;
                continue;
            }

            // 1. Unclosed backticks (inline code spans)
            int backtickCount = 0;
            int lastBacktickCol = -1;
            for (int c = 0; c < line.Length; c++)
            {
                if (line[c] == '`' && (c == 0 || line[c - 1] != '\\'))
                {
                    backtickCount++;
                    lastBacktickCol = c + 1;
                }
            }
            if (backtickCount % 2 != 0)
            {
                unclosedCode++;
                string snippet = line.Length > 40 ? line.Substring(0, 37) + "..." : line;
                ambiguities.Add(new DelimiterBalanceItem(
                    i + 1, lastBacktickCol, "`", "Unclosed inline code span (odd backtick count)", snippet, line + "`"));
            }

            // Strip code spans for subsequent checks so code internals don't trigger false positives
            string stripped = System.Text.RegularExpressions.Regex.Replace(line, @"`[^`]*`", "");

            // 2. Unclosed inline math ($ ... $)
            int mathCount = 0;
            int lastMathCol = -1;
            for (int c = 0; c < stripped.Length; c++)
            {
                if (stripped[c] == '$' && (c == 0 || stripped[c - 1] != '\\'))
                {
                    // Avoid currency pattern like $100 or $5.50
                    if (c + 1 < stripped.Length && char.IsDigit(stripped[c + 1]))
                        continue;
                    // Avoid $$
                    if (c + 1 < stripped.Length && stripped[c + 1] == '$')
                    {
                        c++;
                        continue;
                    }
                    mathCount++;
                    lastMathCol = c + 1;
                }
            }
            if (mathCount % 2 != 0)
            {
                unclosedMath++;
                string snippet = line.Length > 40 ? line.Substring(0, 37) + "..." : line;
                ambiguities.Add(new DelimiterBalanceItem(
                    i + 1, lastMathCol, "$", "Unclosed inline math delimiter (odd $ count)", snippet, line + "$"));
            }

            // 3. Unmatched brackets [ and ]
            int openBrackets = 0;
            int closeBrackets = 0;
            int lastBracketCol = -1;
            for (int c = 0; c < stripped.Length; c++)
            {
                if (stripped[c] == '[' && (c == 0 || stripped[c - 1] != '\\'))
                {
                    openBrackets++;
                    lastBracketCol = c + 1;
                }
                else if (stripped[c] == ']' && (c == 0 || stripped[c - 1] != '\\'))
                {
                    closeBrackets++;
                    lastBracketCol = c + 1;
                }
            }
            if (openBrackets != closeBrackets)
            {
                unmatchedBrackets++;
                string delim = openBrackets > closeBrackets ? "[" : "]";
                string desc = openBrackets > closeBrackets
                    ? "Unclosed opening bracket '[' without matching ']'"
                    : "Unmatched closing bracket ']' without opening '['";
                string repair = openBrackets > closeBrackets ? line + "]" : "[" + line;
                string snippet = line.Length > 40 ? line.Substring(0, 37) + "..." : line;
                ambiguities.Add(new DelimiterBalanceItem(i + 1, lastBracketCol, delim, desc, snippet, repair));
            }

            // 4. Unclosed emphasis (* or **)
            int starCount = 0;
            int lastStarCol = -1;
            for (int c = 0; c < stripped.Length; c++)
            {
                if (stripped[c] == '*' && (c == 0 || stripped[c - 1] != '\\'))
                {
                    // If it's a bullet list item at start of line, ignore
                    if (c == 0 && c + 1 < stripped.Length && char.IsWhiteSpace(stripped[c + 1]))
                        continue;
                    starCount++;
                    lastStarCol = c + 1;
                }
            }
            if (starCount % 2 != 0)
            {
                unclosedEmphasis++;
                string snippet = line.Length > 40 ? line.Substring(0, 37) + "..." : line;
                ambiguities.Add(new DelimiterBalanceItem(
                    i + 1, lastStarCol, "*", "Unclosed emphasis delimiter (odd asterisk count)", snippet, line + "*"));
            }

            // 5. Nested collisions (e.g. *[bold*] or [*bold*])
            if (System.Text.RegularExpressions.Regex.IsMatch(stripped, @"\*\[[^\]]*\*\]") ||
                System.Text.RegularExpressions.Regex.IsMatch(stripped, @"\[\*[^\*]*\]\*"))
            {
                ambiguities.Add(new DelimiterBalanceItem(
                    i + 1, 1, "*[]", "Nested emphasis and link bracket collision detected", line, line.Replace("*[", "[*")));
            }
        }

        int totalAmbiguities = ambiguities.Count;
        double integrityScore = Math.Round(Math.Clamp(100.0 - (totalAmbiguities * 12.5), 0.0, 100.0), 1);

        string category;
        if (totalAmbiguities == 0)
        {
            category = "Optimal Markdown Syntax (100% Balanced)";
        }
        else if (totalAmbiguities <= 2)
        {
            category = "Minor Inline Ambiguities";
        }
        else if (totalAmbiguities <= 5)
        {
            category = "Moderate Delimiter Mismatch";
        }
        else
        {
            category = "Severe Syntax Collision Risk";
        }

        string summary = FormatSyntaxAmbiguitySummary(
            totalAmbiguities, unclosedCode, unclosedEmphasis, unclosedMath, unmatchedBrackets,
            integrityScore, category, ambiguities);

        return new SyntaxAmbiguityAudit(
            totalAmbiguities,
            unclosedCode,
            unclosedEmphasis,
            unclosedMath,
            unmatchedBrackets,
            integrityScore,
            ambiguities,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown syntax ambiguity and delimiter balance.
    /// </summary>
    public static string FormatSyntaxAmbiguitySummary(string? markdownText)
        => AuditSyntaxAmbiguityAndDelimiterBalance(markdownText).Summary;

    public static string FormatSyntaxAmbiguitySummary(
        int totalAmbiguities, int codeSpans, int emphasis, int math, int brackets,
        double integrityScore, string category, IReadOnlyList<DelimiterBalanceItem> ambiguities)
    {
        if (totalAmbiguities == 0)
            return "Syntax ambiguity audit: 0 issues · All inline delimiters (*, `, $, []) are 100% balanced";

        string issueDesc = totalAmbiguities == 1 ? "1 ambiguity" : $"{totalAmbiguities} ambiguities";
        var parts = new List<string>();
        if (codeSpans > 0) parts.Add($"{codeSpans} unclosed code `");
        if (emphasis > 0) parts.Add($"{emphasis} unclosed emphasis *");
        if (math > 0) parts.Add($"{math} unclosed math $");
        if (brackets > 0) parts.Add($"{brackets} unmatched brackets []");
        string breakdown = string.Join(", ", parts);

        string firstIssue = "";
        if (ambiguities.Count > 0)
        {
            var a = ambiguities[0];
            firstIssue = $" · Line {a.LineNumber}: {a.Description}";
        }

        return $"Syntax ambiguity audit ({issueDesc} [{breakdown}] · {category} · {integrityScore:0.#}% integrity){firstIssue}";
    }

    /// <summary>
    /// Audits markdown text for LaTeX math formula syntax, KaTeX delimiter pairing ($...$, $$...$$, \(...\), \[...\]),
    /// curly brace nesting balance, environment tag closure (\begin{...}...\end{...}), and mathematical complexity.
    /// </summary>
    public static MathFormulaAudit AuditMathFormulaComplexityAndDelimiters(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "LaTeX math formula audit: 0 equations · Document contains no math blocks or inline delimiters";
            return new MathFormulaAudit(0, 0, 0, 0, 0, 0.0, Array.Empty<MathFormulaItem>(), Array.Empty<string>(), "No Formulas (Clean)", 100.0, emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var formulas = new List<MathFormulaItem>();
        var globalIssues = new List<string>();

        bool inCodeFence = false;
        bool inHtmlComment = false;
        bool inDisplayMathBlock = false;
        int displayMathStartLine = 0;
        string displayMathDelimiter = "";
        var displayMathBuffer = new System.Text.StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string rawLine = lines[i];
            string trimmed = rawLine.Trim();

            // 1. Code fence check
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            // 2. HTML comment check
            if (trimmed.StartsWith("<!--") && !trimmed.Contains("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }

            // 3. Multiline display math block ($$ or \[)
            if (inDisplayMathBlock)
            {
                if ((displayMathDelimiter == "$$" && trimmed.EndsWith("$$")) ||
                    (displayMathDelimiter == "\\[" && trimmed.EndsWith("\\]")))
                {
                    // Closing display block
                    string content = trimmed;
                    if (displayMathDelimiter == "$$" && content.EndsWith("$$"))
                        content = content.Substring(0, content.Length - 2);
                    else if (displayMathDelimiter == "\\[" && content.EndsWith("\\]"))
                        content = content.Substring(0, content.Length - 2);

                    displayMathBuffer.AppendLine(content);
                    string fullFormula = displayMathBuffer.ToString().Trim();

                    var parsed = AnalyzeFormulaBody(displayMathStartLine, displayMathDelimiter, fullFormula);
                    formulas.Add(parsed);

                    inDisplayMathBlock = false;
                    displayMathBuffer.Clear();
                    continue;
                }
                else
                {
                    displayMathBuffer.AppendLine(rawLine);
                    continue;
                }
            }

            // Check if line begins a multiline display block
            if (trimmed.StartsWith("$$") && !trimmed.Substring(2).Contains("$$"))
            {
                inDisplayMathBlock = true;
                displayMathDelimiter = "$$";
                displayMathStartLine = lineNum;
                displayMathBuffer.Clear();
                displayMathBuffer.AppendLine(trimmed.Substring(2));
                continue;
            }
            if (trimmed.StartsWith("\\[") && !trimmed.Substring(2).Contains("\\]"))
            {
                inDisplayMathBlock = true;
                displayMathDelimiter = "\\[";
                displayMathStartLine = lineNum;
                displayMathBuffer.Clear();
                displayMathBuffer.AppendLine(trimmed.Substring(2));
                continue;
            }

            // 4. Line-level scan (mask out inline code spans)
            // Replace backticked code spans with spaces so we don't look inside them
            var charArray = rawLine.ToCharArray();
            bool insideCodeSpan = false;
            for (int c = 0; c < charArray.Length; c++)
            {
                if (charArray[c] == '`')
                {
                    insideCodeSpan = !insideCodeSpan;
                    charArray[c] = ' ';
                }
                else if (insideCodeSpan)
                {
                    charArray[c] = ' ';
                }
            }
            string scrubbed = new string(charArray);

            int pos = 0;
            while (pos < scrubbed.Length)
            {
                // Check escaped \$
                if (pos > 0 && scrubbed[pos - 1] == '\\' && scrubbed[pos] == '$')
                {
                    pos++;
                    continue;
                }

                // Check display math $$...$$ on same line
                if (pos <= scrubbed.Length - 2 && scrubbed[pos] == '$' && scrubbed[pos + 1] == '$')
                {
                    int closeIdx = scrubbed.IndexOf("$$", pos + 2, StringComparison.Ordinal);
                    if (closeIdx >= 0)
                    {
                        string body = scrubbed.Substring(pos + 2, closeIdx - (pos + 2)).Trim();
                        formulas.Add(AnalyzeFormulaBody(lineNum, "$$", body));
                        pos = closeIdx + 2;
                        continue;
                    }
                    else
                    {
                        globalIssues.Add($"Line {lineNum}: Unclosed display math delimiter '$$'");
                        pos += 2;
                        continue;
                    }
                }

                // Check display math \[ ... \] on same line
                if (pos <= scrubbed.Length - 2 && scrubbed[pos] == '\\' && scrubbed[pos + 1] == '[')
                {
                    int closeIdx = scrubbed.IndexOf("\\]", pos + 2, StringComparison.Ordinal);
                    if (closeIdx >= 0)
                    {
                        string body = scrubbed.Substring(pos + 2, closeIdx - (pos + 2)).Trim();
                        formulas.Add(AnalyzeFormulaBody(lineNum, "\\[", body));
                        pos = closeIdx + 2;
                        continue;
                    }
                    else
                    {
                        globalIssues.Add($"Line {lineNum}: Unclosed display math delimiter '\\['");
                        pos += 2;
                        continue;
                    }
                }

                // Check inline math \( ... \)
                if (pos <= scrubbed.Length - 2 && scrubbed[pos] == '\\' && scrubbed[pos + 1] == '(')
                {
                    int closeIdx = scrubbed.IndexOf("\\)", pos + 2, StringComparison.Ordinal);
                    if (closeIdx >= 0)
                    {
                        string body = scrubbed.Substring(pos + 2, closeIdx - (pos + 2)).Trim();
                        formulas.Add(AnalyzeFormulaBody(lineNum, "\\(", body));
                        pos = closeIdx + 2;
                        continue;
                    }
                    else
                    {
                        globalIssues.Add($"Line {lineNum}: Unclosed inline math delimiter '\\('");
                        pos += 2;
                        continue;
                    }
                }

                // Check inline math $...$
                if (scrubbed[pos] == '$')
                {
                    // KaTeX rule: opening $ cannot be followed by space or digit (currency heuristic)
                    if (pos + 1 < scrubbed.Length && !char.IsWhiteSpace(scrubbed[pos + 1]) && scrubbed[pos + 1] != '$')
                    {
                        int closeIdx = -1;
                        for (int k = pos + 1; k < scrubbed.Length; k++)
                        {
                            if (scrubbed[k] == '$' && scrubbed[k - 1] != '\\')
                            {
                                // Closing $ cannot be preceded by whitespace
                                if (!char.IsWhiteSpace(scrubbed[k - 1]))
                                {
                                    // Also cannot be followed immediately by a digit (avoid $10 and $20)
                                    if (k + 1 >= scrubbed.Length || !char.IsDigit(scrubbed[k + 1]))
                                    {
                                        closeIdx = k;
                                        break;
                                    }
                                }
                            }
                        }

                        if (closeIdx > pos)
                        {
                            string body = scrubbed.Substring(pos + 1, closeIdx - (pos + 1)).Trim();
                            formulas.Add(AnalyzeFormulaBody(lineNum, "$", body));
                            pos = closeIdx + 1;
                            continue;
                        }
                    }
                }

                pos++;
            }
        }

        if (inDisplayMathBlock)
        {
            globalIssues.Add($"Line {displayMathStartLine}: Unclosed multiline display math block '{displayMathDelimiter}' reaching end of document");
        }

        int totalFormulas = formulas.Count;
        int inlineCount = formulas.Count(f => f.DelimiterType == "$" || f.DelimiterType == "\\(");
        int displayCount = formulas.Count(f => f.DelimiterType == "$$" || f.DelimiterType == "\\[");
        int validCount = formulas.Count(f => f.IsBalanced);
        int problemCount = formulas.Count(f => !f.IsBalanced) + globalIssues.Count;

        double meanComplexity = totalFormulas > 0
            ? Math.Round(formulas.Average(f => f.ComplexityScore), 1)
            : 0.0;

        double syntaxIntegrityScore = totalFormulas > 0
            ? Math.Round(Math.Clamp(100.0 * validCount / totalFormulas - (globalIssues.Count * 15.0), 0.0, 100.0), 1)
            : 100.0;

        string category;
        if (totalFormulas == 0)
        {
            category = "No Formulas (Clean)";
        }
        else if (problemCount > 0)
        {
            category = "LaTeX Syntax Errors Detected";
        }
        else if (meanComplexity >= 12.0)
        {
            category = "Advanced Mathematical Complexity";
        }
        else if (meanComplexity >= 4.0)
        {
            category = "Moderate Formula Complexity";
        }
        else
        {
            category = "Light Mathematical Notation";
        }

        string summary = FormatMathFormulaSummary(
            totalFormulas, inlineCount, displayCount, validCount, problemCount,
            meanComplexity, syntaxIntegrityScore, category, formulas, globalIssues);

        return new MathFormulaAudit(
            totalFormulas,
            inlineCount,
            displayCount,
            validCount,
            problemCount,
            meanComplexity,
            formulas,
            globalIssues,
            category,
            syntaxIntegrityScore,
            summary);
    }

    private static MathFormulaItem AnalyzeFormulaBody(int lineNumber, string delimiter, string body)
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(body))
        {
            issues.Add("Empty formula body");
            return new MathFormulaItem(lineNumber, delimiter, "(empty)", false, 0, 0, 0.0, issues);
        }

        int braceDepth = 0;
        int maxBraceDepth = 0;
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '{' && (i == 0 || body[i - 1] != '\\'))
            {
                braceDepth++;
                if (braceDepth > maxBraceDepth) maxBraceDepth = braceDepth;
            }
            else if (body[i] == '}' && (i == 0 || body[i - 1] != '\\'))
            {
                braceDepth--;
                if (braceDepth < 0)
                {
                    issues.Add("Extra closing brace '}' without matching '{'");
                    braceDepth = 0;
                }
            }
        }
        if (braceDepth > 0)
        {
            issues.Add($"{braceDepth} unclosed curly brace{(braceDepth == 1 ? "" : "s")} '{{'");
        }

        // Environment match: \begin{env} ... \end{env}
        var beginMatches = System.Text.RegularExpressions.Regex.Matches(body, @"\\begin\{([a-zA-Z*]+)\}");
        var endMatches = System.Text.RegularExpressions.Regex.Matches(body, @"\\end\{([a-zA-Z*]+)\}");
        if (beginMatches.Count != endMatches.Count)
        {
            issues.Add($"Mismatched environments: {beginMatches.Count} \\begin vs {endMatches.Count} \\end");
        }
        else
        {
            for (int e = 0; e < beginMatches.Count; e++)
            {
                string bName = beginMatches[e].Groups[1].Value;
                string eName = endMatches[e].Groups[1].Value;
                if (!string.Equals(bName, eName, StringComparison.Ordinal))
                {
                    issues.Add($"Environment mismatch: \\begin{{{bName}}} closed with \\end{{{eName}}}");
                }
            }
        }

        // Count mathematical operators and constructs
        int operatorCount = 0;
        char[] basicOps = { '+', '-', '=', '<', '>', '/', '*' };
        foreach (char c in body)
        {
            if (Array.IndexOf(basicOps, c) >= 0) operatorCount++;
        }
        var specialOps = System.Text.RegularExpressions.Regex.Matches(body, @"\\(frac|sqrt|int|sum|prod|lim|times|cdot|div|pm|partial|nabla|alpha|beta|gamma|theta|lambda|pi|sigma|infty|approx|equiv|neq|leq|geq)");
        operatorCount += specialOps.Count;

        // Subscripts / superscripts
        foreach (char c in body)
        {
            if (c == '^' || c == '_') operatorCount++;
        }

        double complexity = Math.Round(Math.Min(10.0, body.Length / 8.0) + (operatorCount * 1.2) + (maxBraceDepth * 1.5) + (beginMatches.Count * 2.5), 1);
        bool isBalanced = issues.Count == 0;

        string snippet = body.Length <= 40 ? body : body.Substring(0, 37) + "...";
        return new MathFormulaItem(lineNumber, delimiter, snippet, isBalanced, maxBraceDepth, operatorCount, complexity, issues);
    }

    /// <summary>
    /// Formats a human-readable summary of LaTeX math formula complexity and delimiters.
    /// </summary>
    public static string FormatMathFormulaSummary(string? markdownText)
        => AuditMathFormulaComplexityAndDelimiters(markdownText).Summary;

    public static string FormatMathFormulaSummary(
        int totalFormulas, int inlineCount, int displayCount, int validCount, int problemCount,
        double meanComplexity, double syntaxIntegrityScore, string category,
        IReadOnlyList<MathFormulaItem> formulas, IReadOnlyList<string> globalIssues)
    {
        if (totalFormulas == 0)
            return "LaTeX math formula audit: 0 equations · Document contains no math blocks or inline delimiters";

        string formDesc = totalFormulas == 1 ? "1 formula" : $"{totalFormulas} formulas";
        string breakdown = $"{inlineCount} inline, {displayCount} display";

        if (problemCount == 0)
        {
            return $"LaTeX math formula audit ({formDesc} [{breakdown}] · {category} · 100% valid): ~{meanComplexity:0.#} avg complexity score";
        }

        string firstProblem = "";
        if (globalIssues.Count > 0)
        {
            firstProblem = $" · {globalIssues[0]}";
        }
        else
        {
            var p = formulas.FirstOrDefault(f => !f.IsBalanced);
            if (p != null && p.SyntaxIssues.Count > 0)
            {
                firstProblem = $" · Line {p.LineNumber}: {p.SyntaxIssues[0]} in '{p.FormulaSnippet}'";
            }
        }

        return $"LaTeX math formula audit ({problemCount}/{totalFormulas} issues · {syntaxIntegrityScore:0.#}% valid [{breakdown}] · {category}): ~{meanComplexity:0.#} avg complexity{firstProblem}";
    }

    /// <summary>
    /// Audits GFM markdown tables for column alignment consistency, cell wrapping risks,
    /// delimiter pipe padding, and ragged row anomalies.
    /// </summary>
    public static TableAlignmentAndSpacingAudit AuditTableAlignmentAndColumnSpacing(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown table alignment audit: 0 tables · Document contains no markdown tables";
            return new TableAlignmentAndSpacingAudit(
                0, 0, 0, 0, 0, 0, 100.0, Array.Empty<TableDetailInfo>(), Array.Empty<string>(), "No Tables", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var tables = new List<TableDetailInfo>();
        var globalIssues = new List<string>();

        bool inCodeFence = false;
        bool inHtmlComment = false;
        var currentBlock = new List<(int LineNumber, string Line)>();

        void ProcessTableBlock()
        {
            if (currentBlock.Count < 2)
            {
                currentBlock.Clear();
                return;
            }

            // Split rows into pipe cells
            var parsedRows = new List<(int LineNumber, List<string> Cells, bool HasOuterPipes, int UnpaddedCount)>();
            foreach (var item in currentBlock)
            {
                string raw = item.Line.Trim();
                bool hasOuter = raw.StartsWith("|") && raw.EndsWith("|");
                string inner = raw;
                if (inner.StartsWith("|")) inner = inner.Substring(1);
                if (inner.EndsWith("|") && inner.Length > 0) inner = inner.Substring(0, inner.Length - 1);

                // Count unpadded pipes inside raw
                int unpadded = 0;
                for (int c = 0; c < raw.Length; c++)
                {
                    if (raw[c] == '|')
                    {
                        bool leftPadded = c == 0 || char.IsWhiteSpace(raw[c - 1]) || raw[c - 1] == '|';
                        bool rightPadded = c == raw.Length - 1 || char.IsWhiteSpace(raw[c + 1]) || raw[c + 1] == '|';
                        if (!leftPadded || !rightPadded) unpadded++;
                    }
                }

                // Split by unescaped pipe
                var cells = new List<string>();
                var cellBuffer = new System.Text.StringBuilder();
                for (int c = 0; c < inner.Length; c++)
                {
                    if (inner[c] == '\\' && c + 1 < inner.Length && inner[c + 1] == '|')
                    {
                        cellBuffer.Append('|');
                        c++;
                    }
                    else if (inner[c] == '|')
                    {
                        cells.Add(cellBuffer.ToString());
                        cellBuffer.Clear();
                    }
                    else
                    {
                        cellBuffer.Append(inner[c]);
                    }
                }
                cells.Add(cellBuffer.ToString());
                parsedRows.Add((item.LineNumber, cells, hasOuter, unpadded));
            }

            // Find delimiter row index (usually index 1)
            int delimiterIdx = -1;
            for (int r = 1; r < Math.Min(3, parsedRows.Count); r++)
            {
                var cells = parsedRows[r].Cells;
                if (cells.Count > 0 && cells.All(c =>
                {
                    string t = c.Trim();
                    return t.Length >= 3 && t.All(ch => ch == '-' || ch == ':' || ch == ' ');
                }))
                {
                    delimiterIdx = r;
                    break;
                }
            }

            if (delimiterIdx < 1)
            {
                currentBlock.Clear();
                return;
            }

            int expectedCols = parsedRows[delimiterIdx].Cells.Count;
            var alignments = new List<string>();
            var alignmentSpecs = new List<string>();

            foreach (var delCell in parsedRows[delimiterIdx].Cells)
            {
                string dt = delCell.Trim();
                alignmentSpecs.Add(dt);
                bool left = dt.StartsWith(":");
                bool right = dt.EndsWith(":");
                if (left && right) alignments.Add("center");
                else if (left) alignments.Add("left");
                else if (right) alignments.Add("right");
                else alignments.Add("default");
            }

            int raggedCount = 0;
            int totalDataRows = 0;
            int totalCells = 0;
            int unpaddedPipesTotal = parsedRows.Sum(p => p.UnpaddedCount);

            var colHeaderWidths = new int[expectedCols];
            var colMinCellWidths = new int[expectedCols];
            var colMaxCellWidths = new int[expectedCols];
            var colTotalCellWidths = new int[expectedCols];
            var colCellCounts = new int[expectedCols];
            var colTruncationRisks = new bool[expectedCols];
            var colUnpaddedCounts = new int[expectedCols];

            for (int c = 0; c < expectedCols; c++)
            {
                colMinCellWidths[c] = int.MaxValue;
                if (c < parsedRows[0].Cells.Count)
                {
                    int hw = parsedRows[0].Cells[c].Trim().Length;
                    colHeaderWidths[c] = hw;
                    colMinCellWidths[c] = hw;
                    colMaxCellWidths[c] = hw;
                    colTotalCellWidths[c] = hw;
                    colCellCounts[c] = 1;
                }
            }

            for (int r = 0; r < parsedRows.Count; r++)
            {
                if (r == delimiterIdx) continue;
                totalDataRows++;
                var rowCells = parsedRows[r].Cells;
                totalCells += rowCells.Count;

                if (rowCells.Count != expectedCols)
                {
                    raggedCount++;
                }

                for (int c = 0; c < expectedCols && c < rowCells.Count; c++)
                {
                    string cellText = rowCells[c].Trim();
                    int len = cellText.Length;
                    if (len < colMinCellWidths[c]) colMinCellWidths[c] = len;
                    if (len > colMaxCellWidths[c]) colMaxCellWidths[c] = len;
                    colTotalCellWidths[c] += len;
                    colCellCounts[c]++;

                    // Truncation risk: monolithic token > 45 chars or cell > 90 chars
                    if (len > 90 || (len > 45 && !cellText.Contains(' ')))
                    {
                        colTruncationRisks[c] = true;
                    }
                }
            }

            var colMetrics = new List<TableColumnMetrics>();
            for (int c = 0; c < expectedCols; c++)
            {
                int minW = colMinCellWidths[c] == int.MaxValue ? 0 : colMinCellWidths[c];
                int maxW = colMaxCellWidths[c];
                double meanW = colCellCounts[c] > 0 ? Math.Round((double)colTotalCellWidths[c] / colCellCounts[c], 1) : 0.0;
                string align = c < alignments.Count ? alignments[c] : "default";
                colMetrics.Add(new TableColumnMetrics(
                    c + 1, align, colHeaderWidths[c], maxW, minW, meanW, colTruncationRisks[c], colUnpaddedCounts[c]));
            }

            double score = 100.0;
            if (raggedCount > 0) score -= Math.Min(40.0, raggedCount * 15.0);
            if (unpaddedPipesTotal > 0) score -= Math.Min(25.0, unpaddedPipesTotal * 2.5);
            if (colTruncationRisks.Any(t => t)) score -= 15.0;
            score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

            int startLine = currentBlock[0].LineNumber;
            int endLine = currentBlock[^1].LineNumber;
            bool properPadding = unpaddedPipesTotal == 0;

            tables.Add(new TableDetailInfo(
                startLine, endLine, expectedCols, totalDataRows, raggedCount, totalCells,
                score, colMetrics, alignmentSpecs, properPadding));

            currentBlock.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                if (currentBlock.Count > 0) ProcessTableBlock();
                continue;
            }
            if (inCodeFence) continue;

            if (trimmed.StartsWith("<!--") && !trimmed.EndsWith("-->"))
            {
                inHtmlComment = true;
                if (currentBlock.Count > 0) ProcessTableBlock();
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;

            if (trimmed.Contains("|") && !string.IsNullOrWhiteSpace(trimmed))
            {
                currentBlock.Add((i + 1, line));
            }
            else
            {
                if (currentBlock.Count > 0) ProcessTableBlock();
            }
        }

        if (currentBlock.Count > 0) ProcessTableBlock();

        if (tables.Count == 0)
        {
            string emptySummary = "Markdown table alignment audit: 0 tables · Document contains no markdown tables";
            return new TableAlignmentAndSpacingAudit(
                0, 0, 0, 0, 0, 0, 100.0, Array.Empty<TableDetailInfo>(), Array.Empty<string>(), "No Tables", emptySummary);
        }

        int totalTables = tables.Count;
        int totalRows = tables.Sum(t => t.TotalRows);
        int totalCols = tables.Sum(t => t.ColumnCount);
        int totalCellsCount = tables.Sum(t => t.CellCount);
        int totalRagged = tables.Sum(t => t.RaggedRowCount);
        int totalUnpadded = tables.Sum(t => t.Columns.Sum(c => c.UnpaddedPipesCount));
        double meanScore = Math.Round(tables.Average(t => t.AlignmentIntegrityScore), 1);

        string category;
        if (totalRagged == 0 && meanScore >= 95.0)
        {
            category = "Consistent Column Alignment (100% Valid)";
        }
        else if (totalRagged == 0)
        {
            category = "Properly Aligned (Minor Padding Variances)";
        }
        else if (totalRagged <= 2)
        {
            category = "Minor Ragged Rows Detected";
        }
        else
        {
            category = "Severe Table Structural Discrepancies";
        }

        if (totalRagged > 0)
        {
            globalIssues.Add($"{totalRagged} ragged row{(totalRagged == 1 ? "" : "s")} with missing or extra column cells");
        }
        if (tables.Any(t => !t.HasProperPadding))
        {
            globalIssues.Add("Unpadded delimiter pipe boundaries detected");
        }
        if (tables.Any(t => t.Columns.Any(c => c.HasTruncationRisk)))
        {
            globalIssues.Add("Wide cell wrapping or truncation hazards detected");
        }

        string summary = FormatTableAlignmentAndColumnSpacingSummary(
            totalTables, totalRows, totalCols, totalRagged, meanScore, category, globalIssues, tables);

        return new TableAlignmentAndSpacingAudit(
            totalTables, totalRows, totalCols, totalCellsCount, totalRagged, totalUnpadded,
            meanScore, tables, globalIssues, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown table column alignment and pipe spacing.
    /// </summary>
    public static string FormatTableAlignmentAndColumnSpacingSummary(string? markdownText)
        => AuditTableAlignmentAndColumnSpacing(markdownText).Summary;

    public static string FormatTableAlignmentAndColumnSpacingSummary(
        int totalTables, int totalRows, int totalColumns, int totalRagged,
        double meanScore, string category, IReadOnlyList<string> issues,
        IReadOnlyList<TableDetailInfo> tables)
    {
        if (totalTables == 0)
            return "Markdown table alignment audit: 0 tables · Document contains no markdown tables";

        string tableDesc = totalTables == 1 ? "1 table" : $"{totalTables} tables";
        int maxCols = tables.Count > 0 ? tables.Max(t => t.ColumnCount) : 0;
        string colDesc = $"{totalRows} rows, max {maxCols} cols";

        if (totalRagged == 0 && issues.Count == 0)
        {
            return $"Markdown table alignment audit ({tableDesc} [{colDesc}] · {category} · {meanScore:0.#}% score): Fully aligned GFM grids";
        }

        string firstIssue = issues.Count > 0 ? $" · {issues[0]}" : "";
        return $"Markdown table alignment audit ({tableDesc} [{colDesc}] · {category} · {meanScore:0.#}% score){firstIssue}";
    }

    /// <summary>
    /// Audits markdown definition list syntax (:::deflist blocks and standard Term\n: Definition pairs),
    /// measuring hanging indent consistency, missing definitions, and structural uniformity.
    /// </summary>
    public static DefinitionListAudit AuditDefinitionListConsistency(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new DefinitionListAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<DefinitionItemInfo>(),
                Array.Empty<string>(),
                "No Definition Lists",
                "Markdown definition list audit: 0 definition lists · Document contains no definition lists");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inFence = false;
        string fenceMarker = "";
        bool inDefListBlock = false;

        var items = new List<DefinitionItemInfo>();
        var issues = new List<string>();
        int totalLists = 0;
        int fencedLists = 0;
        int looseLists = 0;
        int totalTerms = 0;
        int totalDefinitions = 0;
        int irregularIndents = 0;

        string? pendingTerm = null;
        bool listActive = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string rawLine = lines[i];
            string trimmed = rawLine.Trim();
            int lineNum = i + 1;

            if (!inFence)
            {
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = true;
                    fenceMarker = trimmed.Substring(0, 3);
                    continue;
                }
            }
            else
            {
                if (trimmed.StartsWith(fenceMarker))
                {
                    inFence = false;
                }
                continue;
            }

            if (trimmed.StartsWith(":::deflist", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("::: deflist", StringComparison.OrdinalIgnoreCase))
            {
                inDefListBlock = true;
                fencedLists++;
                totalLists++;
                listActive = true;
                continue;
            }
            if (inDefListBlock && trimmed == ":::")
            {
                inDefListBlock = false;
                listActive = false;
                pendingTerm = null;
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            bool isDefLine = (trimmed.StartsWith(":") || trimmed.StartsWith("~")) &&
                             trimmed.Length > 1 &&
                             (trimmed[1] == ' ' || trimmed[1] == '\t');

            if (isDefLine)
            {
                totalDefinitions++;
                string term = pendingTerm ?? "(anonymous term)";
                if (pendingTerm == null)
                {
                    issues.Add($"Line {lineNum}: Definition found without preceding term");
                }

                int colonIdx = rawLine.IndexOfAny(new[] { ':', '~' });
                int indentSpaces = 0;
                if (colonIdx >= 0)
                {
                    int p = colonIdx + 1;
                    while (p < rawLine.Length && (rawLine[p] == ' ' || rawLine[p] == '\t'))
                    {
                        indentSpaces += rawLine[p] == '\t' ? 4 : 1;
                        p++;
                    }
                }

                bool isIrregular = indentSpaces < 2 || indentSpaces > 4;
                if (isIrregular)
                {
                    irregularIndents++;
                    issues.Add($"Line {lineNum}: Irregular hanging indent ({indentSpaces} spaces) for term '{term}'");
                }

                string defSnippet = trimmed.Substring(1).Trim();
                if (defSnippet.Length > 60) defSnippet = defSnippet.Substring(0, 57) + "...";

                items.Add(new DefinitionItemInfo(
                    lineNum,
                    term,
                    defSnippet,
                    indentSpaces,
                    inDefListBlock,
                    isIrregular));

                if (!listActive)
                {
                    looseLists++;
                    totalLists++;
                    listActive = true;
                }
            }
            else
            {
                if (!trimmed.StartsWith("#") && !trimmed.StartsWith(">") && !trimmed.StartsWith("|") && !trimmed.StartsWith("- ") && !trimmed.StartsWith("* "))
                {
                    pendingTerm = trimmed;
                    totalTerms++;
                }
                else
                {
                    pendingTerm = null;
                    listActive = false;
                }
            }
        }

        if (totalLists == 0 && totalDefinitions == 0)
        {
            return new DefinitionListAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<DefinitionItemInfo>(),
                Array.Empty<string>(),
                "No Definition Lists",
                "Markdown definition list audit: 0 definition lists · Document contains no definition lists");
        }

        double uniformityScore = totalDefinitions > 0
            ? Math.Max(0.0, Math.Round(100.0 - (irregularIndents * 25.0 / totalDefinitions), 1))
            : 100.0;

        string category;
        if (irregularIndents == 0)
            category = "Strict Uniform Indent (100%)";
        else if (uniformityScore >= 80.0)
            category = "High Consistency (≥80%)";
        else if (uniformityScore >= 50.0)
            category = "Moderate Indent Jumps (50–79%)";
        else
            category = "Ragged / Inconsistent Indents (<50%)";

        string summary = FormatDefinitionListSummary(
            totalLists, totalTerms, totalDefinitions, fencedLists, looseLists,
            irregularIndents, uniformityScore, category, issues);

        return new DefinitionListAudit(
            totalLists, totalTerms, totalDefinitions, fencedLists, looseLists,
            irregularIndents, uniformityScore, items, issues, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown definition list audit results from markdown text.
    /// </summary>
    public static string FormatDefinitionListSummary(string? markdownText)
        => AuditDefinitionListConsistency(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of definition list consistency metrics.
    /// </summary>
    public static string FormatDefinitionListSummary(
        int totalLists, int totalTerms, int totalDefinitions, int fencedLists, int looseLists,
        int irregularIndents, double uniformityScore, string category, IReadOnlyList<string>? issues = null)
    {
        if (totalLists == 0 && totalDefinitions == 0)
            return "Markdown definition list audit: 0 definition lists · Document contains no definition lists";

        string listDesc = totalLists == 1 ? "1 definition list" : $"{totalLists} definition lists";
        string breakdown = $"{totalTerms} terms, {totalDefinitions} definitions";
        string typeDesc = fencedLists > 0 ? $"{fencedLists} fenced, {looseLists} loose" : $"{looseLists} loose";

        if (irregularIndents == 0)
        {
            return $"Markdown definition list audit ({listDesc} [{breakdown}, {typeDesc}] · {category} · {uniformityScore:0.#}% score): Uniform hanging indents";
        }

        string firstIssue = (issues != null && issues.Count > 0) ? $" · {issues[0]}" : "";
        return $"Markdown definition list audit ({listDesc} [{breakdown}, {typeDesc}] · {category} · {uniformityScore:0.#}% score){firstIssue}";
    }

    /// <summary>
    /// Audits markdown documents for footnote and reference anchor consistency, bi-directional linking,
    /// duplicate definitions, unreferenced orphan footnotes, missing targets, and numerical monotonicity.
    /// </summary>
    public static FootnoteReferenceConsistencyAudit AuditFootnoteAndReferenceConsistency(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Footnote reference consistency audit: 0 footnotes · Document contains no footnote references or definitions";
            return new FootnoteReferenceConsistencyAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<FootnoteAnchorItem>(),
                Array.Empty<FootnoteDefinitionDetail>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>(),
                "Clean Baseline (Zero Footnotes)",
                emptySummary);
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        bool inCodeFence = false;
        bool inHtmlComment = false;

        var anchors = new List<FootnoteAnchorItem>();
        var definitions = new List<FootnoteDefinitionDetail>();
        var definedIdCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var anchorIdCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missingIds = new List<string>();
        var orphanIds = new List<string>();
        var duplicateIds = new List<string>();

        var defRegex = new Regex(@"^\s*\[\^([a-zA-Z0-9_\-]+)\]:\s*(.*)$", RegexOptions.Compiled);
        var anchorRegex = new Regex(@"\[\^([a-zA-Z0-9_\-]+)\](?!:)", RegexOptions.Compiled);

        int lastNumericSeen = -1;
        int nonMonotonicCount = 0;
        int appearanceIndex = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string rawLine = lines[i];
            string trimmed = rawLine.Trim();
            int lineNumber = i + 1;

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            if (trimmed.StartsWith("<!--") && !trimmed.EndsWith("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;

            // Check if this line is a footnote definition: [^id]: ...
            var defMatch = defRegex.Match(rawLine);
            if (defMatch.Success)
            {
                string id = defMatch.Groups[1].Value.Trim();
                string content = defMatch.Groups[2].Value.Trim();
                string preview = content.Length > 60 ? content.Substring(0, 57) + "..." : content;

                definedIdCounts[id] = definedIdCounts.TryGetValue(id, out int c) ? c + 1 : 1;
                if (definedIdCounts[id] == 2 && !duplicateIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    duplicateIds.Add(id);
                }

                definitions.Add(new FootnoteDefinitionDetail(
                    lineNumber, id, preview, content.Length, 0, false));
                continue;
            }

            // Outside code fences, strip inline backtick code spans before matching anchors
            string strippedLine = Regex.Replace(rawLine, @"`[^`]*`", "");

            var anchorMatches = anchorRegex.Matches(strippedLine);
            foreach (Match m in anchorMatches)
            {
                string id = m.Groups[1].Value.Trim();
                appearanceIndex++;

                bool isMonotonic = true;
                if (int.TryParse(id, out int num))
                {
                    if (lastNumericSeen >= 0 && num < lastNumericSeen)
                    {
                        isMonotonic = false;
                        nonMonotonicCount++;
                    }
                    else if (num > lastNumericSeen)
                    {
                        lastNumericSeen = num;
                    }
                }

                anchorIdCounts[id] = anchorIdCounts.TryGetValue(id, out int ac) ? ac + 1 : 1;
                anchors.Add(new FootnoteAnchorItem(lineNumber, id, appearanceIndex, isMonotonic));
            }
        }

        // Cross-reference anchors against definitions
        var uniqueAnchorSet = new HashSet<string>(anchors.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        var uniqueDefSet = new HashSet<string>(definitions.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var aid in uniqueAnchorSet)
        {
            if (!uniqueDefSet.Contains(aid))
            {
                missingIds.Add(aid);
            }
        }

        foreach (var did in uniqueDefSet)
        {
            if (!uniqueAnchorSet.Contains(did))
            {
                orphanIds.Add(did);
            }
        }

        // Update definition details with reference count and orphan status
        var detailedDefinitions = new List<FootnoteDefinitionDetail>();
        foreach (var def in definitions)
        {
            int refCount = anchorIdCounts.TryGetValue(def.Id, out int rc) ? rc : 0;
            bool isOrphan = refCount == 0;
            detailedDefinitions.Add(def with { ReferenceCount = refCount, IsOrphan = isOrphan });
        }

        int totalAnchors = anchors.Count;
        int totalDefs = definitions.Count;

        if (totalAnchors == 0 && totalDefs == 0)
        {
            string emptySummary = "Footnote reference consistency audit: 0 footnotes · Document contains no footnote references or definitions";
            return new FootnoteReferenceConsistencyAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                anchors, detailedDefinitions, missingIds, orphanIds, duplicateIds,
                "Clean Baseline (Zero Footnotes)", emptySummary);
        }

        double score = 100.0;
        score -= missingIds.Count * 25.0;
        score -= duplicateIds.Count * 15.0;
        score -= orphanIds.Count * 10.0;
        score -= nonMonotonicCount * 5.0;
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category;
        if (missingIds.Count == 0 && orphanIds.Count == 0 && duplicateIds.Count == 0 && nonMonotonicCount == 0)
        {
            category = "Perfect Bi-Directional Consistency";
        }
        else if (missingIds.Count == 0 && orphanIds.Count == 0 && duplicateIds.Count == 0 && nonMonotonicCount > 0)
        {
            category = "Minor Monotonicity Gaps";
        }
        else if (missingIds.Count > 0)
        {
            category = "Missing Footnote Targets";
        }
        else if (orphanIds.Count > 0)
        {
            category = "Orphan Footnotes Present";
        }
        else
        {
            category = "Mixed Footnote Inconsistencies";
        }

        string summary = FormatFootnoteReferenceSummary(
            totalAnchors, totalDefs, orphanIds.Count, missingIds.Count, duplicateIds.Count, score, category);

        return new FootnoteReferenceConsistencyAudit(
            totalAnchors,
            uniqueAnchorSet.Count,
            totalDefs,
            uniqueDefSet.Count,
            orphanIds.Count,
            missingIds.Count,
            duplicateIds.Count,
            nonMonotonicCount,
            score,
            anchors,
            detailedDefinitions,
            missingIds,
            orphanIds,
            duplicateIds,
            category,
            summary);
    }

    /// <summary>
    /// Audits markdown documents for footnote and reference anchor integrity, bi-directional linking,
    /// duplicate definitions, unreferenced orphan footnotes, missing targets, and numerical monotonicity.
    /// </summary>
    public static FootnoteReferenceConsistencyAudit AuditFootnoteReferenceIntegrity(string? markdownText)
        => AuditFootnoteAndReferenceConsistency(markdownText);

    /// <summary>
    /// Formats a human-readable summary of footnote and reference consistency metrics from markdown text.
    /// </summary>
    public static string FormatFootnoteReferenceSummary(string? markdownText)
        => AuditFootnoteAndReferenceConsistency(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of footnote and reference consistency metrics.
    /// </summary>
    public static string FormatFootnoteReferenceSummary(
        int totalAnchors, int totalDefinitions, int unreferenced, int missing, int duplicates,
        double consistencyScore, string category)
    {
        if (totalAnchors == 0 && totalDefinitions == 0)
            return "Footnote reference consistency audit: 0 footnotes · Document contains no footnote references or definitions";

        string anchorLabel = totalAnchors == 1 ? "1 anchor" : $"{totalAnchors} anchors";
        string defLabel = totalDefinitions == 1 ? "1 definition" : $"{totalDefinitions} definitions";

        var alerts = new List<string>();
        if (missing > 0) alerts.Add($"🚨 {missing} missing target{(missing == 1 ? "" : "s")}");
        if (unreferenced > 0) alerts.Add($"⚠ {unreferenced} orphan{(unreferenced == 1 ? "" : "s")}");
        if (duplicates > 0) alerts.Add($"⚠ {duplicates} duplicate{(duplicates == 1 ? "" : "s")}");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · Fully linked";

        return $"Footnote reference consistency audit ({anchorLabel}, {defLabel} · {category} · {consistencyScore:0.#}% score){alertSuffix}";
    }

    /// <summary>
    /// Audits markdown text for GitHub Flavored Markdown (GFM) and Obsidian admonition callout syntax
    /// (> [!NOTE], > [!TIP], > [!IMPORTANT], > [!WARNING], > [!CAUTION]), nesting depth, spacing consistency,
    /// foldability markers, and content body emptiness.
    /// </summary>
    public static AdmonitionCalloutAudit AuditAdmonitionCalloutConsistency(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown admonition callout audit: 0 callouts · Document contains no admonitions";
            return new AdmonitionCalloutAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<AdmonitionCalloutItem>(),
                Array.Empty<string>(),
                "No Admonitions",
                emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var callouts = new List<AdmonitionCalloutItem>();
        var issues = new List<string>();

        bool inCodeFence = false;
        string fenceMarker = string.Empty;

        var standardTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NOTE", "TIP", "IMPORTANT", "WARNING", "CAUTION"
        };

        var extendedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INFO", "HINT", "DANGER", "ERROR", "FAQ", "ABSTRACT", "SUMMARY", "TLDR", "EXAMPLE", "QUOTE", "BUG", "QUESTION", "SUCCESS", "FAILURE"
        };

        var calloutHeaderRegex = new Regex(@"^[ \t]*(>+)[ \t]*\[!([a-zA-Z0-9_\-]+)\]([+-]?)(?:[ \t]+(.*))?$", RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                if (!inCodeFence)
                {
                    inCodeFence = true;
                    fenceMarker = tag;
                }
                else if (tag == fenceMarker || trimmed == ":::")
                {
                    inCodeFence = false;
                    fenceMarker = string.Empty;
                }
                continue;
            }

            if (inCodeFence) continue;

            var match = calloutHeaderRegex.Match(line);
            if (!match.Success) continue;

            var chevronGroup = match.Groups[1].Value;
            int nestingDepth = chevronGroup.Length;
            string rawType = match.Groups[2].Value;
            string foldChar = match.Groups[3].Value;
            string title = match.Groups[4].Success ? match.Groups[4].Value.Trim() : string.Empty;

            bool isStandard = standardTypes.Contains(rawType);
            bool isRecognized = isStandard || extendedTypes.Contains(rawType);
            bool isFoldable = !string.IsNullOrEmpty(foldChar);
            bool? foldExpanded = foldChar == "+" ? true : (foldChar == "-" ? false : null);

            int endOfChevrons = match.Groups[1].Index + match.Groups[1].Length;
            int indexOfBracket = line.IndexOf("[!", endOfChevrons, StringComparison.Ordinal);
            bool hasProperSpacing = (indexOfBracket > endOfChevrons) &&
                                    char.IsWhiteSpace(line[endOfChevrons]);

            int bodyLineCount = 0;
            int j = i + 1;
            while (j < lines.Length)
            {
                var nextLine = lines[j];
                var nextTrimmed = nextLine.Trim();

                if (nextTrimmed.StartsWith("```") || nextTrimmed.StartsWith("~~~") || nextTrimmed.StartsWith(":::"))
                    break;

                if (calloutHeaderRegex.IsMatch(nextLine))
                    break;

                var nextChevrons = nextLine.TakeWhile(c => c == ' ' || c == '\t' || c == '>').Count(c => c == '>');
                if (nextChevrons >= nestingDepth)
                {
                    string remainder = nextLine.TrimStart(' ', '\t', '>');
                    if (!string.IsNullOrWhiteSpace(remainder))
                    {
                        bodyLineCount++;
                    }
                    j++;
                }
                else if (string.IsNullOrWhiteSpace(nextLine))
                {
                    if (j + 1 < lines.Length && lines[j + 1].TrimStart().StartsWith(">"))
                    {
                        j++;
                        continue;
                    }
                    break;
                }
                else
                {
                    if (nextTrimmed.StartsWith("#") || nextTrimmed.StartsWith("- ") || nextTrimmed.StartsWith("* ") || nextTrimmed.StartsWith("1."))
                        break;
                    bodyLineCount++;
                    j++;
                }
            }

            var item = new AdmonitionCalloutItem(
                LineNumber: i + 1,
                Tag: rawType.ToUpperInvariant(),
                Title: title,
                NestingDepth: nestingDepth,
                HasProperSpacing: hasProperSpacing,
                IsStandardType: isStandard,
                IsFoldable: isFoldable,
                FoldableExpanded: foldExpanded,
                ContentLineCount: bodyLineCount);

            callouts.Add(item);

            if (!hasProperSpacing)
            {
                issues.Add($"Line {i + 1}: Missing space after blockquote chevron in '>[!{rawType}]'");
            }
            if (!isRecognized)
            {
                issues.Add($"Line {i + 1}: Non-standard callout type '[!{rawType}]'");
            }
            if (nestingDepth > 2)
            {
                issues.Add($"Line {i + 1}: Deeply nested callout (depth {nestingDepth})");
            }
            if (bodyLineCount == 0 && string.IsNullOrWhiteSpace(title))
            {
                issues.Add($"Line {i + 1}: Empty callout body with no content lines");
            }
        }

        int totalCallouts = callouts.Count;
        if (totalCallouts == 0)
        {
            string emptySummary = "Markdown admonition callout audit: 0 callouts · Document contains no admonitions";
            return new AdmonitionCalloutAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                callouts, issues, "No Admonitions", emptySummary);
        }

        int standardCallouts = callouts.Count(c => c.IsStandardType);
        int customCallouts = callouts.Count(c => !c.IsStandardType);
        int nestedCallouts = callouts.Count(c => c.NestingDepth > 1);
        int maxNestingDepth = callouts.Max(c => c.NestingDepth);
        int improperSpacing = callouts.Count(c => !c.HasProperSpacing);
        int emptyBodyCount = callouts.Count(c => c.ContentLineCount == 0 && string.IsNullOrWhiteSpace(c.Title));

        double penalty = (improperSpacing * 15.0) + (emptyBodyCount * 20.0) + (customCallouts * 5.0) + (Math.Max(0, maxNestingDepth - 2) * 10.0);
        double parityScore = Math.Clamp(100.0 - (penalty / totalCallouts), 0.0, 100.0);

        string category = parityScore switch
        {
            >= 95.0 when improperSpacing == 0 && emptyBodyCount == 0 => "Pristine GFM Admonition Syntax",
            >= 80.0 => "Minor Callout Syntax Variance",
            >= 60.0 => "Improper Spacing or Non-Standard Types",
            _ => "Malformed or Empty Callouts"
        };

        string summary = FormatAdmonitionCalloutSummary(
            totalCallouts, standardCallouts, customCallouts, nestedCallouts,
            maxNestingDepth, improperSpacing, emptyBodyCount, parityScore, category);

        return new AdmonitionCalloutAudit(
            TotalCallouts: totalCallouts,
            StandardCallouts: standardCallouts,
            CustomCallouts: customCallouts,
            NestedCallouts: nestedCallouts,
            MaxNestingDepth: maxNestingDepth,
            ImproperSpacingCount: improperSpacing,
            EmptyBodyCount: emptyBodyCount,
            ParityScore: Math.Round(parityScore, 1),
            Callouts: callouts,
            Issues: issues,
            Category: category,
            Summary: summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown admonition callout consistency metrics.
    /// </summary>
    public static string FormatAdmonitionCalloutSummary(string? markdownText)
        => AuditAdmonitionCalloutConsistency(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of markdown admonition callout consistency metrics.
    /// </summary>
    public static string FormatAdmonitionCalloutSummary(
        int totalCallouts, int standardCallouts, int customCallouts, int nestedCallouts,
        int maxNestingDepth, int improperSpacing, int emptyBody, double parityScore, string category)
    {
        if (totalCallouts == 0)
            return "Markdown admonition callout audit: 0 callouts · Document contains no admonitions";

        string calloutLabel = totalCallouts == 1 ? "1 callout" : $"{totalCallouts} callouts";

        var alerts = new List<string>();
        if (improperSpacing > 0) alerts.Add($"⚠ {improperSpacing} spacing defect{(improperSpacing == 1 ? "" : "s")}");
        if (emptyBody > 0) alerts.Add($"🚨 {emptyBody} empty body{(emptyBody == 1 ? "" : "ies")}");
        if (customCallouts > 0) alerts.Add($"{customCallouts} non-standard");
        if (nestedCallouts > 0) alerts.Add($"{nestedCallouts} nested (max depth {maxNestingDepth})");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · All GFM standard types";

        return $"Markdown admonition callout audit ({calloutLabel} · {category} · {parityScore:0.#}% score){alertSuffix}";
    }

    /// <summary>
    /// Audits markdown blockquote callout admonitions, verifying GitHub-style alert syntax ([!NOTE], [!TIP], [!IMPORTANT], [!WARNING], [!CAUTION]),
    /// unsupported/custom callout types, spacing defects, and excessive blockquote nesting depth (>= 3 levels).
    /// </summary>
    public static AdmonitionCalloutAudit AuditBlockquoteCalloutsAndDepth(string? markdownText)
        => AuditAdmonitionCalloutConsistency(markdownText);

    /// <summary>
    /// Formats a human-readable summary of blockquote callout syntax and nesting depth metrics.
    /// </summary>
    public static string FormatBlockquoteCalloutSummary(string? markdownText)
        => FormatAdmonitionCalloutSummary(markdownText);

    /// <summary>
    /// Audits markdown and inline HTML image tags for WCAG non-text content compliance, missing or generic alt-text,
    /// valid image file extensions, and insecure HTTP vs secure HTTPS image URLs.
    /// Excludes fenced code blocks and inline code spans to prevent code syntax from triggering false positives.
    /// </summary>
    public static ImageAccessibilityAudit AuditImageLinkAndAltTextAccessibility(string? markdownText)
    {
        var images = new List<ImageAccessibilityItem>();
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new ImageAccessibilityAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                images, issues, "No Images Detected",
                "Markdown image accessibility audit: 0 images · Document contains no embedded media");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        bool inFence = false;
        string fenceMarker = string.Empty;

        var mdImgRegex = new Regex(@"!\[(?<alt>[^\]]*)\]\((?<url>[^)\s]+)(?:\s+""(?<title>[^""]*)"")?\)", RegexOptions.Compiled);
        var htmlImgRegex = new Regex(@"<img\s+[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var htmlSrcRegex = new Regex(@"src\s*=\s*[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var htmlAltRegex = new Regex(@"alt\s*=\s*[""'](?<alt>[^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var genericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image", "img", "photo", "picture", "screenshot", "graphic", "untitled", "icon", "banner", "figure", "drawing", "diagram"
        };

        var validExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".svg", ".webp", ".gif", ".bmp", ".ico", ".tiff", ".avif"
        };

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = tag;
                }
                else if (tag == fenceMarker || trimmed == ":::")
                {
                    inFence = false;
                    fenceMarker = string.Empty;
                }
                continue;
            }

            if (inFence) continue;

            // Strip inline code `...`
            string lineWithoutCode = Regex.Replace(line, @"`[^`]*`", " ");

            // 1. Markdown Images ![alt](url)
            foreach (Match m in mdImgRegex.Matches(lineWithoutCode))
            {
                string alt = m.Groups["alt"].Value.Trim();
                string url = m.Groups["url"].Value.Trim();
                ProcessImageItem(i + 1, alt, url, isHtml: false);
            }

            // 2. HTML Images <img ...>
            foreach (Match m in htmlImgRegex.Matches(lineWithoutCode))
            {
                string tag = m.Value;
                var srcMatch = htmlSrcRegex.Match(tag);
                if (!srcMatch.Success) continue;
                string url = srcMatch.Groups["url"].Value.Trim();
                var altMatch = htmlAltRegex.Match(tag);
                string alt = altMatch.Success ? altMatch.Groups["alt"].Value.Trim() : string.Empty;
                ProcessImageItem(i + 1, alt, url, isHtml: true);
            }
        }

        void ProcessImageItem(int lineNumber, string alt, string url, bool isHtml)
        {
            bool hasAlt = !string.IsNullOrWhiteSpace(alt);
            bool isGeneric = hasAlt && genericNames.Contains(alt);
            bool isDataUri = url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
            bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            bool isHttps = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            bool isLocalRelative = !isDataUri && !isHttp && !isHttps;

            string ext = "";
            if (!isDataUri)
            {
                try
                {
                    int queryIdx = url.IndexOfAny(new[] { '?', '#' });
                    string cleanPath = queryIdx >= 0 ? url.Substring(0, queryIdx) : url;
                    ext = Path.GetExtension(cleanPath);
                }
                catch { }
            }
            bool hasValidExt = isDataUri || (!string.IsNullOrEmpty(ext) && validExtensions.Contains(ext));

            string? warning = null;
            if (!hasAlt)
            {
                warning = "Missing alt-text description (WCAG 1.1.1 failure)";
                issues.Add($"Line {lineNumber}: Missing alt-text for image '{TruncateUrl(url)}'");
            }
            else if (isGeneric)
            {
                warning = $"Generic placeholder alt-text '{alt}'";
                issues.Add($"Line {lineNumber}: Non-descriptive generic alt-text '{alt}'");
            }
            else if (isHttp)
            {
                warning = "Insecure HTTP image URL (mixed content hazard)";
                issues.Add($"Line {lineNumber}: Insecure HTTP image '{TruncateUrl(url)}'");
            }
            else if (!hasValidExt && !isLocalRelative)
            {
                warning = "Missing standard image file extension";
                issues.Add($"Line {lineNumber}: Image URL lacks standard image extension");
            }

            images.Add(new ImageAccessibilityItem(
                LineNumber: lineNumber,
                AltText: alt,
                SourceUrl: url,
                IsHtmlTag: isHtml,
                HasAltText: hasAlt,
                HasGenericAltText: isGeneric,
                IsLocalRelative: isLocalRelative,
                IsSecureWeb: isHttps,
                IsInsecureHttp: isHttp,
                IsDataUri: isDataUri,
                HasValidImageExtension: hasValidExt,
                WarningMessage: warning));
        }

        static string TruncateUrl(string u) => u.Length <= 40 ? u : u.Substring(0, 37) + "…";

        int totalImages = images.Count;
        if (totalImages == 0)
        {
            return new ImageAccessibilityAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                images, issues, "No Images Detected",
                "Markdown image accessibility audit: 0 images · Document contains no embedded media");
        }

        int missingAlt = images.Count(img => !img.HasAltText);
        int genericAlt = images.Count(img => img.HasGenericAltText);
        int insecureHttp = images.Count(img => img.IsInsecureHttp);
        int dataUris = images.Count(img => img.IsDataUri);
        int localRelative = images.Count(img => img.IsLocalRelative);
        int invalidExt = images.Count(img => !img.HasValidImageExtension);
        int validAccessible = images.Count(img => img.HasAltText && !img.HasGenericAltText && !img.IsInsecureHttp);

        double penalty = (missingAlt * 25.0) + (genericAlt * 10.0) + (insecureHttp * 10.0) + (invalidExt * 5.0);
        double score = Math.Clamp(100.0 - (penalty / totalImages), 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 when missingAlt == 0 && genericAlt == 0 => "WCAG AAA Compliant Image Content",
            >= 80.0 => "Accessible with Minor Polish Opportunities",
            >= 60.0 => "Missing Alt-Text or Insecure Protocols",
            _ => "Non-Compliant Image Accessibility Hazards"
        };

        string summary = FormatImageAccessibilitySummary(
            totalImages, validAccessible, missingAlt, genericAlt, insecureHttp, dataUris, localRelative, score, category);

        return new ImageAccessibilityAudit(
            TotalImages: totalImages,
            ValidAccessibleImages: validAccessible,
            MissingAltCount: missingAlt,
            GenericAltCount: genericAlt,
            BrokenOrMalformedCount: invalidExt,
            InsecureHttpCount: insecureHttp,
            DataUriCount: dataUris,
            LocalRelativeCount: localRelative,
            AccessibilityScore: Math.Round(score, 1),
            Images: images,
            Issues: issues,
            ComplianceCategory: category,
            Summary: summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown image accessibility and alt-text compliance metrics.
    /// </summary>
    public static string FormatImageAccessibilitySummary(string? markdownText)
        => AuditImageLinkAndAltTextAccessibility(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of markdown image accessibility and alt-text compliance metrics.
    /// </summary>
    public static string FormatImageAccessibilitySummary(
        int totalImages, int validAccessible, int missingAlt, int genericAlt, int insecureHttp,
        int dataUris, int localRelative, double score, string category)
    {
        if (totalImages == 0)
            return "Markdown image accessibility audit: 0 images · Document contains no embedded media";

        string imgLabel = totalImages == 1 ? "1 image" : $"{totalImages} images";
        var alerts = new List<string>();
        if (missingAlt > 0) alerts.Add($"🚨 {missingAlt} missing alt-text");
        if (genericAlt > 0) alerts.Add($"⚠ {genericAlt} generic alt");
        if (insecureHttp > 0) alerts.Add($"🔓 {insecureHttp} insecure http");
        if (dataUris > 0) alerts.Add($"{dataUris} data-uri");
        if (localRelative > 0) alerts.Add($"{localRelative} local relative");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% descriptive alt-text";

        return $"Markdown image accessibility audit ({imgLabel} · {category} · {score:0.#}% compliance){alertSuffix}";
    }

    /// <summary>
    /// Audits markdown text for LaTeX math delimiters ($...$, $$...$$, \(...\), \[...\]),
    /// delimiter pairing, unbalanced curly braces, KaTeX macro syntax, and spacing compliance.
    /// Excludes fenced and inline code blocks to prevent false positive code variables.
    /// </summary>
    public static MathDelimiterAndMacroAudit AuditMathDelimiterAndMacroIntegrity(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new MathDelimiterAndMacroAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MathDelimiterItem>(),
                Array.Empty<string>(),
                "No Math Formulas",
                "LaTeX math delimiter & macro audit: 0 formulas · Document contains no math notation");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var maskedLines = new string[lines.Length];
        bool inFence = false;
        string fenceMarker = string.Empty;

        // Step 1: Mask fenced code blocks and inline code spans preserving line numbers and column offsets
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = tag;
                }
                else if (tag == fenceMarker || trimmed == ":::")
                {
                    inFence = false;
                    fenceMarker = string.Empty;
                }
                maskedLines[i] = new string(' ', line.Length);
                continue;
            }

            if (inFence)
            {
                maskedLines[i] = new string(' ', line.Length);
                continue;
            }

            // Mask inline code spans `...`
            var maskedChars = line.ToCharArray();
            bool inBacktick = false;
            for (int c = 0; c < maskedChars.Length; c++)
            {
                if (maskedChars[c] == '`')
                {
                    inBacktick = !inBacktick;
                    maskedChars[c] = ' ';
                }
                else if (inBacktick)
                {
                    maskedChars[c] = ' ';
                }
            }
            maskedLines[i] = new string(maskedChars);
        }

        var mathItems = new List<MathDelimiterItem>();
        var globalIssues = new List<string>();
        int inlineCount = 0;
        int displayCount = 0;
        int unbalancedDelims = 0;
        int unbalancedBracesTotal = 0;
        int invalidSpacingCount = 0;
        int unknownMacroTotal = 0;

        // Known standard KaTeX / LaTeX macros and symbols
        var knownMacros = new HashSet<string>(StringComparer.Ordinal)
        {
            "frac", "dfrac", "tfrac", "sqrt", "sum", "int", "iint", "iiint", "oint", "prod", "coprod",
            "alpha", "beta", "gamma", "delta", "epsilon", "varepsilon", "zeta", "eta", "theta", "vartheta",
            "iota", "kappa", "lambda", "mu", "nu", "xi", "pi", "varpi", "rho", "varrho", "sigma", "varsigma",
            "tau", "upsilon", "phi", "varphi", "chi", "psi", "omega",
            "Gamma", "Delta", "Theta", "Lambda", "Xi", "Pi", "Sigma", "Upsilon", "Phi", "Psi", "Omega",
            "partial", "infty", "pm", "mp", "times", "div", "ast", "star", "circ", "bullet", "cdot",
            "cap", "cup", "uplus", "sqcap", "sqcup", "vee", "wedge", "setminus", "wr", "diamond",
            "leq", "le", "geq", "ge", "equiv", "models", "prec", "succ", "sim", "perp", "preceq", "succeq",
            "approx", "parallel", "subset", "supset", "subseteq", "supseteq", "in", "ni", "notin",
            "to", "rightarrow", "leftarrow", "Rightarrow", "Leftarrow", "leftrightarrow", "Leftrightarrow",
            "mapsto", "hookleftarrow", "hookrightarrow", "nearrow", "searrow", "swarrow", "nwarrow",
            "left", "right", "big", "Big", "bigg", "Bigg", "bigl", "bigr", "Bigl", "Bigr",
            "mathbf", "mathit", "mathrm", "mathsf", "mathtt", "mathbb", "mathcal", "mathscr", "mathfrak",
            "text", "textbf", "textit", "textrm", "textsf", "texttt",
            "vec", "hat", "check", "tilde", "acute", "grave", "dot", "ddot", "breve", "bar",
            "overbrace", "underbrace", "overline", "underline", "widehat", "widetilde",
            "sin", "cos", "tan", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh",
            "cot", "coth", "sec", "csc", "log", "ln", "lg", "exp", "lim", "liminf", "limsup",
            "min", "max", "inf", "sup", "det", "dim", "gcd", "hom", "ker", "deg", "arg",
            "begin", "end", "ce", "pu", "color", "textcolor", "over", "atop", "ldots", "cdots", "vdots", "ddots",
            "quad", "qquad", "phantom", "hphantom", "vphantom", "hspace", "vspace", "rule",
            "substack", "binom", "dbinom", "tbinom", "operatorname", "bmod", "pmod",
            "nabla", "forall", "exists", "nexists", "neg", "angle", "hbar", "ell", "Re", "Im", "aleph",
            "matrix", "pmatrix", "bmatrix", "Bmatrix", "vmatrix", "Vmatrix", "array", "aligned", "cases"
        };

        // Combine masked lines for multi-line display math scanning ($$...$$ and \[...\])
        string fullMasked = string.Join("\n", maskedLines);

        // Regex for Display Math: $$...$$ and \[...\]
        var displayRegex = new Regex(@"(?<!\\)(?:\$\$(.*?)\$\$|\\\[(.*?)\\\])", RegexOptions.Singleline);
        var displayMatches = displayRegex.Matches(fullMasked);
        var maskedForInlineChars = fullMasked.ToCharArray();

        foreach (Match match in displayMatches)
        {
            displayCount++;
            int startIdx = match.Index;
            int lineNumber = fullMasked.Substring(0, startIdx).Count(c => c == '\n') + 1;
            string rawContent = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            string delimType = match.Value.StartsWith("$$") ? "$$...$$" : "\\[...\\]";

            // Mask out matched display math so inline scanner won't see it
            for (int k = match.Index; k < match.Index + match.Length; k++)
            {
                maskedForInlineChars[k] = ' ';
            }

            var itemIssues = new List<string>();
            var unrecMacros = new List<string>();

            // Check curly brace balance inside math
            int braceDepth = 0;
            for (int b = 0; b < rawContent.Length; b++)
            {
                if (rawContent[b] == '\\' && b + 1 < rawContent.Length && (rawContent[b + 1] == '{' || rawContent[b + 1] == '}'))
                {
                    b++; // escaped brace
                    continue;
                }
                if (rawContent[b] == '{') braceDepth++;
                else if (rawContent[b] == '}')
                {
                    braceDepth--;
                    if (braceDepth < 0)
                    {
                        itemIssues.Add("Extra closing curly brace '}' without opening '{'");
                    }
                }
            }

            if (braceDepth != 0)
            {
                unbalancedBracesTotal++;
                itemIssues.Add($"Unbalanced curly braces (depth delta: {braceDepth})");
            }

            // Inspect macros inside rawContent
            var macroMatches = Regex.Matches(rawContent, @"\\([a-zA-Z]+)");
            foreach (Match mm in macroMatches)
            {
                string mName = mm.Groups[1].Value;
                if (!knownMacros.Contains(mName) && !unrecMacros.Contains(mName))
                {
                    unrecMacros.Add(mName);
                }
            }

            if (unrecMacros.Count > 0)
            {
                unknownMacroTotal += unrecMacros.Count;
                itemIssues.Add($"Unrecognized macro(s): {string.Join(", ", unrecMacros.Select(m => "\\" + m))}");
            }

            // Check environment matching (\begin{x} ... \end{x})
            var envBegins = Regex.Matches(rawContent, @"\\begin\{([a-zA-Z*]+)\}");
            var envEnds = Regex.Matches(rawContent, @"\\end\{([a-zA-Z*]+)\}");
            if (envBegins.Count != envEnds.Count)
            {
                itemIssues.Add($"Mismatched LaTeX environments (\\begin count: {envBegins.Count}, \\end count: {envEnds.Count})");
            }

            bool isBalanced = itemIssues.Count == 0;
            string snippet = rawContent.Trim().Replace('\n', ' ');
            if (snippet.Length > 40) snippet = snippet.Substring(0, 37) + "…";

            mathItems.Add(new MathDelimiterItem(
                LineNumber: lineNumber,
                DelimiterType: delimType,
                Snippet: snippet,
                IsBalanced: isBalanced,
                CurlyBraceDepth: braceDepth,
                HasInvalidSpacing: false,
                UnrecognizedMacros: unrecMacros,
                Issues: itemIssues));
        }

        // Check for unclosed $$ or \[
        string remainingAfterDisplay = new string(maskedForInlineChars);
        int unclosedDoubleDollar = Regex.Matches(remainingAfterDisplay, @"(?<!\\)\$\$").Count;
        if (unclosedDoubleDollar > 0)
        {
            unbalancedDelims += unclosedDoubleDollar;
            globalIssues.Add($"Unclosed display math delimiter ($$ count: {unclosedDoubleDollar})");
        }
        int unclosedBracketMath = Regex.Matches(remainingAfterDisplay, @"(?<!\\)\\\[").Count;
        if (unclosedBracketMath > 0)
        {
            unbalancedDelims += unclosedBracketMath;
            globalIssues.Add($"Unclosed LaTeX display math bracket (\\[)");
        }

        // Step 2: Scan inline math on remaining text line-by-line: \(...\) and $...$
        var remainingLines = remainingAfterDisplay.Split('\n');
        for (int i = 0; i < remainingLines.Length; i++)
        {
            string line = remainingLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            // 2A: LaTeX \(...\) inline math
            var parenRegex = new Regex(@"(?<!\\)\\\((.*?)(?<!\\)\\\)");
            var parenMatches = parenRegex.Matches(line);
            foreach (Match m in parenMatches)
            {
                inlineCount++;
                string content = m.Groups[1].Value;
                var itemIssues = new List<string>();
                var unrecMacros = new List<string>();

                int braceDepth = 0;
                for (int b = 0; b < content.Length; b++)
                {
                    if (content[b] == '\\' && b + 1 < content.Length && (content[b + 1] == '{' || content[b + 1] == '}'))
                    {
                        b++;
                        continue;
                    }
                    if (content[b] == '{') braceDepth++;
                    else if (content[b] == '}') braceDepth--;
                }
                if (braceDepth != 0)
                {
                    unbalancedBracesTotal++;
                    itemIssues.Add($"Unbalanced curly braces in \\(...\\)");
                }

                var macroMatches = Regex.Matches(content, @"\\([a-zA-Z]+)");
                foreach (Match mm in macroMatches)
                {
                    string mName = mm.Groups[1].Value;
                    if (!knownMacros.Contains(mName) && !unrecMacros.Contains(mName))
                    {
                        unrecMacros.Add(mName);
                    }
                }
                if (unrecMacros.Count > 0)
                {
                    unknownMacroTotal += unrecMacros.Count;
                    itemIssues.Add($"Unrecognized macro(s): {string.Join(", ", unrecMacros.Select(m => "\\" + m))}");
                }

                string snippet = content.Trim();
                if (snippet.Length > 30) snippet = snippet.Substring(0, 27) + "…";

                mathItems.Add(new MathDelimiterItem(
                    LineNumber: i + 1,
                    DelimiterType: "\\(...\\)",
                    Snippet: snippet,
                    IsBalanced: itemIssues.Count == 0,
                    CurlyBraceDepth: braceDepth,
                    HasInvalidSpacing: false,
                    UnrecognizedMacros: unrecMacros,
                    Issues: itemIssues));
            }

            // Check unclosed \(
            int unclosedParenMath = Regex.Matches(parenRegex.Replace(line, " "), @"(?<!\\)\\\(").Count;
            if (unclosedParenMath > 0)
            {
                unbalancedDelims += unclosedParenMath;
                globalIssues.Add($"Line {i + 1}: Unclosed LaTeX inline math parenthesis (\\()");
            }

            // 2B: Single dollar $...$ inline math
            // KaTeX standard: opening $ cannot be followed by space, closing $ cannot be preceded by space.
            // Also ignore currency patterns like $100 or $50.00 followed by space or punctuation.
            var lineWithoutParens = parenRegex.Replace(line, " ");
            int idx = 0;
            while (idx < lineWithoutParens.Length)
            {
                int openIdx = lineWithoutParens.IndexOf('$', idx);
                if (openIdx == -1) break;

                // Check if escaped: \$
                if (openIdx > 0 && lineWithoutParens[openIdx - 1] == '\\')
                {
                    idx = openIdx + 1;
                    continue;
                }

                // KaTeX rule: space cannot immediately follow opening $
                bool openSpace = openIdx + 1 < lineWithoutParens.Length && lineWithoutParens[openIdx + 1] == ' ';

                // Find closing $ (cannot be preceded by \, cannot be immediately followed by digit)
                int closeIdx = -1;
                for (int c = openIdx + 1; c < lineWithoutParens.Length; c++)
                {
                    if (lineWithoutParens[c] == '$' && lineWithoutParens[c - 1] != '\\')
                    {
                        // A closing $ cannot be immediately followed by a digit (KaTeX/Pandoc currency disambiguation)
                        if (c + 1 < lineWithoutParens.Length && char.IsDigit(lineWithoutParens[c + 1]))
                        {
                            continue;
                        }
                        closeIdx = c;
                        break;
                    }
                }

                if (closeIdx == -1)
                {
                    // If opening $ is immediately followed by a digit, it is a standalone currency amount (e.g. $50, $100)
                    if (openIdx + 1 < lineWithoutParens.Length && char.IsDigit(lineWithoutParens[openIdx + 1]))
                    {
                        idx = openIdx + 1;
                        continue;
                    }

                    // Unclosed single dollar
                    unbalancedDelims++;
                    globalIssues.Add($"Line {i + 1}: Unclosed inline math dollar ($)");
                    break;
                }

                bool closeSpace = closeIdx > openIdx + 1 && lineWithoutParens[closeIdx - 1] == ' ';
                bool invalidSpacing = openSpace || closeSpace;
                if (invalidSpacing) invalidSpacingCount++;

                inlineCount++;
                string content = lineWithoutParens.Substring(openIdx + 1, closeIdx - openIdx - 1);
                var itemIssues = new List<string>();
                var unrecMacros = new List<string>();

                if (invalidSpacing)
                {
                    itemIssues.Add("Invalid spacing (opening $ followed by space, or closing $ preceded by space)");
                }

                int braceDepth = 0;
                for (int b = 0; b < content.Length; b++)
                {
                    if (content[b] == '\\' && b + 1 < content.Length && (content[b + 1] == '{' || content[b + 1] == '}'))
                    {
                        b++;
                        continue;
                    }
                    if (content[b] == '{') braceDepth++;
                    else if (content[b] == '}') braceDepth--;
                }
                if (braceDepth != 0)
                {
                    unbalancedBracesTotal++;
                    itemIssues.Add($"Unbalanced curly braces in $...$");
                }

                var macroMatches = Regex.Matches(content, @"\\([a-zA-Z]+)");
                foreach (Match mm in macroMatches)
                {
                    string mName = mm.Groups[1].Value;
                    if (!knownMacros.Contains(mName) && !unrecMacros.Contains(mName))
                    {
                        unrecMacros.Add(mName);
                    }
                }
                if (unrecMacros.Count > 0)
                {
                    unknownMacroTotal += unrecMacros.Count;
                    itemIssues.Add($"Unrecognized macro(s): {string.Join(", ", unrecMacros.Select(m => "\\" + m))}");
                }

                string snippet = content.Trim();
                if (snippet.Length > 25) snippet = snippet.Substring(0, 22) + "…";

                mathItems.Add(new MathDelimiterItem(
                    LineNumber: i + 1,
                    DelimiterType: "$...$",
                    Snippet: snippet,
                    IsBalanced: itemIssues.Count == 0,
                    CurlyBraceDepth: braceDepth,
                    HasInvalidSpacing: invalidSpacing,
                    UnrecognizedMacros: unrecMacros,
                    Issues: itemIssues));

                idx = closeIdx + 1;
            }
        }

        int totalSpans = mathItems.Count;
        int validSpans = mathItems.Count(m => m.IsBalanced);
        int syntaxIssues = mathItems.Count(m => !m.IsBalanced) + globalIssues.Count;

        double baseScore = totalSpans > 0
            ? (100.0 * validSpans / totalSpans)
            : (syntaxIssues > 0 ? 50.0 : 100.0);

        double penalty = (unbalancedDelims * 20.0) + (unbalancedBracesTotal * 10.0) +
                         (unknownMacroTotal * 5.0) + (invalidSpacingCount * 5.0);

        double score = Math.Round(Math.Clamp(baseScore - penalty, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 when syntaxIssues == 0 => "Full KaTeX & MathML Render Fidelity",
            >= 80.0 => "Good Interoperability with Minor Warnings",
            >= 60.0 => "Syntax Warnings Requiring Review",
            _ => "Critical Delimiter or Macro Render Hazards"
        };

        string summary = FormatMathDelimiterSummary(
            totalSpans, inlineCount, displayCount, validSpans, syntaxIssues,
            unbalancedDelims, unbalancedBracesTotal, invalidSpacingCount, unknownMacroTotal, score, category);

        return new MathDelimiterAndMacroAudit(
            TotalMathSpans: totalSpans,
            InlineMathCount: inlineCount,
            DisplayMathCount: displayCount,
            ValidMathCount: validSpans,
            SyntaxIssueCount: syntaxIssues,
            UnbalancedDelimiters: unbalancedDelims,
            UnbalancedBraces: unbalancedBracesTotal,
            InvalidSpacingCount: invalidSpacingCount,
            UnknownMacroCount: unknownMacroTotal,
            KaTeXInteropScore: Math.Round(score, 1),
            MathItems: mathItems,
            GlobalIssues: globalIssues,
            CompatibilityCategory: category,
            Summary: summary);
    }

    /// <summary>
    /// Formats a human-readable summary of LaTeX math delimiter and KaTeX macro integrity metrics.
    /// </summary>
    public static string FormatMathDelimiterSummary(string? markdownText)
        => AuditMathDelimiterAndMacroIntegrity(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of LaTeX math delimiter and KaTeX macro integrity metrics.
    /// </summary>
    public static string FormatMathDelimiterSummary(
        int totalSpans, int inlineCount, int displayCount, int validCount, int syntaxIssues,
        int unbalancedDelims, int unbalancedBraces, int invalidSpacing, int unknownMacros,
        double score, string category)
    {
        if (totalSpans == 0 && syntaxIssues == 0)
            return "LaTeX math delimiter & macro audit: 0 formulas · Document contains no math notation";

        string formulaLabel = totalSpans == 1 ? "1 formula" : $"{totalSpans} formulas";
        var alerts = new List<string>();
        if (unbalancedDelims > 0) alerts.Add($"🚨 {unbalancedDelims} unclosed delimiter(s)");
        if (unbalancedBraces > 0) alerts.Add($"⚠ {unbalancedBraces} unbalanced brace(s)");
        if (invalidSpacing > 0) alerts.Add($"📐 {invalidSpacing} invalid spacing");
        if (unknownMacros > 0) alerts.Add($"🔍 {unknownMacros} unknown macro(s)");

        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% KaTeX syntax parity";

        return $"LaTeX math delimiter audit ({formulaLabel} · {displayCount} display, {inlineCount} inline · {category} · {score:0.#}% interop){alertSuffix}";
    }

    /// <summary>
    /// Audits markdown document reading time, syllable counts, Flesch Reading Ease score,
    /// Flesch-Kincaid Grade Level, and estimated silent reading and speaking duration.
    /// Strips code fences, raw HTML, image tags, and LaTeX math blocks to isolate natural language prose.
    /// </summary>
    public static DocumentReadingEaseAudit AuditDocumentReadingEaseAndGradeLevel(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new DocumentReadingEaseAudit(
                0, 0, 0, 0, 0, 100.0, 0.0, 0.0, 0.0, 0.0, 0.0,
                Array.Empty<ReadingGradeMetricInfo>(),
                "Empty Document",
                "Document reading ease audit: 0 words · Document contains no readable prose");
        }

        // 1. Strip code blocks (```...``` and ~~~...~~~)
        string cleaned = System.Text.RegularExpressions.Regex.Replace(
            markdownText, @"(?s)(```|~~~).*?\1", " ");

        // 2. Strip display math blocks ($$...$$ and \[...\])
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"(?s)\$\$.*?\$\$|\\\[.*?\\\]", " ");

        // 3. Strip inline math ($...$ and \(...\))
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"(?s)\$.*?\$|\\\(.*?\\\)", " ");

        // 4. Strip inline code (`...`)
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"`[^`\r\n]+`", " ");

        // 5. Strip HTML tags
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"<[^>]+>", " ");

        // 6. Strip image links ![alt](url)
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"!\[.*?\]\(.*?\)", " ");

        // 7. Unwrap standard markdown links [text](url) -> text
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"\[(.*?)\]\(.*?\)", "$1");

        // 8. Strip markdown headings (# Header -> Header)
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"(?m)^#{1,6}\s+", "");

        // 9. Strip blockquote markers (> )
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"(?m)^>\s*", "");

        // 10. Strip list markers (* , - , 1. )
        cleaned = System.Text.RegularExpressions.Regex.Replace(
            cleaned, @"(?m)^\s*([-*+]|\d+\.)\s+", "");

        // Tokenize sentences
        var sentenceDelimiters = new[] { '.', '!', '?' };
        var rawSentences = cleaned.Split(sentenceDelimiters, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && s.Any(char.IsLetter))
            .ToList();

        // Tokenize words
        var wordMatches = System.Text.RegularExpressions.Regex.Matches(cleaned, @"\b[A-Za-z]+(?:'[A-Za-z]+)?\b");
        var words = wordMatches.Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Value)
            .ToList();

        if (words.Count == 0)
        {
            return new DocumentReadingEaseAudit(
                0, 0, 0, 0, 0, 100.0, 0.0, 0.0, 0.0, 0.0, 0.0,
                Array.Empty<ReadingGradeMetricInfo>(),
                "No Readable Prose",
                "Document reading ease audit: 0 words · Document contains no readable prose");
        }

        int totalWords = words.Count;
        int totalSentences = Math.Max(1, rawSentences.Count);
        int totalSyllables = 0;
        int monosyllableCount = 0;
        int polysyllableCount = 0;

        foreach (var word in words)
        {
            int syllables = CountWordSyllables(word);
            totalSyllables += syllables;
            if (syllables == 1) monosyllableCount++;
            else if (syllables >= 3) polysyllableCount++;
        }

        double wordsPerSentence = Math.Round((double)totalWords / totalSentences, 1);
        double syllablesPerWord = Math.Round((double)totalSyllables / totalWords, 2);

        // Standard Flesch Reading Ease formula
        double rawFlesch = 206.835 - (1.015 * (totalWords / (double)totalSentences)) - (84.6 * (totalSyllables / (double)totalWords));
        double fleschReadingEase = Math.Round(Math.Clamp(rawFlesch, 0.0, 100.0), 1);

        // Flesch-Kincaid Grade Level formula
        double rawGrade = (0.39 * (totalWords / (double)totalSentences)) + (11.8 * (totalSyllables / (double)totalWords)) - 15.59;
        double gradeLevel = Math.Round(Math.Max(0.0, rawGrade), 1);

        // Reading speeds: 225 wpm silent, 140 wpm speaking
        double silentReadingMinutes = Math.Round((double)totalWords / 225.0, 1);
        double speakingMinutes = Math.Round((double)totalWords / 140.0, 1);

        string category;
        if (fleschReadingEase >= 90.0) category = "Very Easy (5th Grade)";
        else if (fleschReadingEase >= 80.0) category = "Easy (6th Grade)";
        else if (fleschReadingEase >= 70.0) category = "Fairly Easy (7th Grade)";
        else if (fleschReadingEase >= 60.0) category = "Standard / Plain English (8th–9th Grade)";
        else if (fleschReadingEase >= 50.0) category = "Fairly Difficult (10th–12th Grade)";
        else if (fleschReadingEase >= 30.0) category = "Difficult (College Level)";
        else category = "Very Difficult / Technical (Graduate Level)";

        var metrics = new List<ReadingGradeMetricInfo>
        {
            new("Flesch Reading Ease", fleschReadingEase, $"{fleschReadingEase:0.#}/100", "60–70 (Plain English)", category),
            new("Flesch-Kincaid Grade Level", gradeLevel, $"Grade {gradeLevel:0.#}", "7.0–9.0 (General Audience)", $"Requires ~{Math.Ceiling(gradeLevel)} years of formal education"),
            new("Average Sentence Length", wordsPerSentence, $"{wordsPerSentence:0.#} words/sentence", "15–20 words", wordsPerSentence > 25 ? "Long complex sentences" : "Well-paced sentences"),
            new("Syllable Complexity", syllablesPerWord, $"{syllablesPerWord:0.##} syllables/word", "1.3–1.6 syllables", syllablesPerWord > 1.8 ? "Heavy multisyllabic vocabulary" : "Accessible vocabulary"),
            new("Silent Reading Duration", silentReadingMinutes, $"~{silentReadingMinutes:0.#} min", "225 words/min", $"{totalWords} total words"),
            new("Spoken Presentation Duration", speakingMinutes, $"~{speakingMinutes:0.#} min", "140 words/min", $"{totalWords} total words")
        };

        string summary = FormatReadingEaseSummary(
            totalWords, totalSentences, totalSyllables, fleschReadingEase, gradeLevel,
            silentReadingMinutes, category);

        return new DocumentReadingEaseAudit(
            totalWords, totalSentences, totalSyllables, monosyllableCount, polysyllableCount,
            fleschReadingEase, gradeLevel, wordsPerSentence, syllablesPerWord,
            silentReadingMinutes, speakingMinutes, metrics, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of document reading ease, Flesch score, and estimated duration.
    /// </summary>
    public static string FormatReadingEaseSummary(string? markdownText)
        => AuditDocumentReadingEaseAndGradeLevel(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of document reading ease, Flesch score, and estimated duration metrics.
    /// </summary>
    public static string FormatReadingEaseSummary(
        int totalWords, int totalSentences, int totalSyllables, double readingEase,
        double gradeLevel, double readingTimeMins, string category)
    {
        if (totalWords == 0)
            return "Document reading ease audit: 0 words · Document contains no readable prose";

        string wordLabel = totalWords == 1 ? "1 word" : $"{totalWords} words";
        string sentenceLabel = totalSentences == 1 ? "1 sentence" : $"{totalSentences} sentences";

        return $"Document reading ease ({wordLabel}, {sentenceLabel} · {category}): " +
               $"Flesch score {readingEase:0.#}/100 · Grade Level {gradeLevel:0.#} · ~{readingTimeMins:0.#} min read";
    }

    /// <summary>
    /// Audits markdown definition lists and glossary terms (Term\n: Definition or <dt>/<dd>),
    /// validating colon prefixes, irregular indentations, orphaned terms, and multi-definition syntax.
    /// </summary>
    public static DefinitionListAndGlossaryAudit AuditDefinitionListsAndGlossaryTerms(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new DefinitionListAndGlossaryAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<DefinitionListItem>(),
                "No Definition Lists",
                "Markdown definition lists & glossary terms: 0 terms · Document contains no definition lists");
        }

        // Strip fenced code blocks
        string cleaned = System.Text.RegularExpressions.Regex.Replace(
            markdownText,
            @"(?m)^(?<fence>`{3,}|~{3,})[^\r\n]*\r?\n[\s\S]*?^\k<fence>\s*$",
            "",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        // Strip HTML comment blocks
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"<!--[\s\S]*?-->", "");

        var lines = cleaned.Replace("\r", "").Split('\n');
        var terms = new List<DefinitionListItem>();

        int totalDefs = 0;
        int orphaned = 0;
        int multiDef = 0;
        int standardIndent = 0;
        int irregularIndent = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Skip empty lines, headings, blockquotes, lists, tables
            if (string.IsNullOrWhiteSpace(trimmed) ||
                trimmed.StartsWith("#") ||
                trimmed.StartsWith(">") ||
                trimmed.StartsWith("- ") ||
                trimmed.StartsWith("* ") ||
                trimmed.StartsWith("+ ") ||
                trimmed.StartsWith("|") ||
                (trimmed.Length > 2 && char.IsDigit(trimmed[0]) && trimmed[1] == '.'))
            {
                continue;
            }

            // Check if the next line is a definition prefix (starts with ':' or '~')
            if (i + 1 < lines.Length)
            {
                string nextLine = lines[i + 1].Trim();
                if (nextLine.StartsWith(":") || nextLine.StartsWith("~"))
                {
                    string termName = trimmed;
                    int termLineNumber = i + 1;
                    var defs = new List<string>();
                    bool hasProperColon = true;
                    bool hasBlankLineBefore = (i == 0 || string.IsNullOrWhiteSpace(lines[i - 1]));

                    int j = i + 1;
                    while (j < lines.Length)
                    {
                        string dLine = lines[j];
                        string dTrim = dLine.Trim();
                        if (dTrim.StartsWith(":") || dTrim.StartsWith("~"))
                        {
                            char prefix = dTrim[0];
                            string rest = dTrim.Substring(1);
                            if (rest.StartsWith(" ") || rest.StartsWith("\t"))
                            {
                                standardIndent++;
                            }
                            else
                            {
                                irregularIndent++;
                                hasProperColon = false;
                            }
                            defs.Add(rest.Trim());
                            j++;
                        }
                        else if (defs.Count > 0 && (dLine.StartsWith("    ") || dLine.StartsWith("\t")))
                        {
                            // Indented continuation line of definition
                            defs[defs.Count - 1] += " " + dTrim;
                            j++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (defs.Count > 1) multiDef++;
                    totalDefs += defs.Count;

                    string status = hasProperColon ? "Valid Definition Syntax" : "Irregular Colon Indentation";
                    terms.Add(new DefinitionListItem(
                        termLineNumber,
                        termName,
                        defs,
                        hasProperColon,
                        hasBlankLineBefore,
                        defs.Count > 1,
                        status));

                    i = j - 1; // Advance past definitions
                }
            }
        }

        // Also check HTML <dl><dt><dd> tags
        var dtMatches = System.Text.RegularExpressions.Regex.Matches(
            cleaned, @"<dt>(?<term>[^<]+)</dt>\s*<dd>(?<def>[^<]+)</dd>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in dtMatches)
        {
            string t = m.Groups["term"].Value.Trim();
            string d = m.Groups["def"].Value.Trim();
            totalDefs++;
            standardIndent++;
            terms.Add(new DefinitionListItem(
                1, t, new[] { d }, true, true, false, "HTML <dl> List Definition"));
        }

        if (terms.Count == 0)
        {
            return new DefinitionListAndGlossaryAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<DefinitionListItem>(),
                "No Definition Lists",
                "Markdown definition lists & glossary terms: 0 terms · Document contains no definition lists");
        }

        double penalty = (orphaned * 25.0) + (irregularIndent * 10.0);
        double score = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 when orphaned == 0 && irregularIndent == 0 => "Pristine Definition List Syntax",
            >= 80.0 => "Well-Structured Glossary Terms",
            >= 60.0 => "Minor Glossary Formatting Defects",
            _ => "Malformed Definition List Syntax"
        };

        string summary = FormatDefinitionListsAndGlossarySummary(
            terms.Count, totalDefs, orphaned, multiDef, irregularIndent, score, category);

        return new DefinitionListAndGlossaryAudit(
            terms.Count,
            totalDefs,
            orphaned,
            multiDef,
            standardIndent,
            irregularIndent,
            score,
            terms,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown definition lists and glossary terms.
    /// </summary>
    public static string FormatDefinitionListsAndGlossarySummary(string? markdownText)
        => AuditDefinitionListsAndGlossaryTerms(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of definition list metrics.
    /// </summary>
    public static string FormatDefinitionListsAndGlossarySummary(
        int totalTerms, int totalDefs, int orphaned, int multiDef, int irregular, double score, string category)
    {
        if (totalTerms == 0)
            return "Markdown definition lists & glossary terms: 0 terms · Document contains no definition lists";

        string termsLabel = totalTerms == 1 ? "1 term" : $"{totalTerms} terms";
        string breakdown = multiDef > 0 ? $"{multiDef} multi-definition" : "single-definition";
        var alerts = new List<string>();
        if (orphaned > 0) alerts.Add($"⚠ {orphaned} orphaned");
        if (irregular > 0) alerts.Add($"⚠ {irregular} irregular indent");
        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · syntax aligned";

        return $"Definition lists & glossary terms ({termsLabel} [{totalDefs} defs, {breakdown}] · {category} · {score:0.#}% score){alertSuffix}";
    }

    /// <summary>
    /// Audits markdown task list progress, subtask nesting hierarchy, completion velocity,
    /// and parent-child checkbox state consistency (GFM task lists: - [ ], - [x], * [ ], 1. [ ]).
    /// Strips code fences prior to evaluation to prevent false positives from code blocks.
    /// </summary>
    public static TaskListHierarchyAudit AuditTaskListHierarchyAndProgress(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new TaskListHierarchyAudit(
                0, 0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<TaskListHierarchyItem>(),
                "No Task Lists",
                "Task list progress & hierarchy: 0 tasks (Empty document) · No GFM task lists detected");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        bool inFence = false;
        var taskRegex = new System.Text.RegularExpressions.Regex(@"^(\s*)(?:[-*+]|\d+\.)\s+\[([ xX\-])\]\s*(.*)$");

        var rawTasks = new List<(int LineNumber, int IndentSpaces, bool IsCompleted, string TaskText)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            var match = taskRegex.Match(line);
            if (match.Success)
            {
                int indentSpaces = match.Groups[1].Value.Length;
                string checkChar = match.Groups[2].Value;
                bool isCompleted = checkChar.Equals("x", StringComparison.OrdinalIgnoreCase);
                string text = match.Groups[3].Value.Trim();
                rawTasks.Add((i + 1, indentSpaces, isCompleted, text));
            }
        }

        if (rawTasks.Count == 0)
        {
            return new TaskListHierarchyAudit(
                0, 0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<TaskListHierarchyItem>(),
                "No Task Lists",
                "Task list progress & hierarchy: 0 tasks · Document contains no GFM task lists");
        }

        // Build hierarchical relationships using indentation levels
        var stack = new Stack<(int indent, int taskIndex)>();
        var subtaskCounts = new int[rawTasks.Count];
        var completedSubtaskCounts = new int[rawTasks.Count];
        var indentLevels = new int[rawTasks.Count];
        var parentIndices = new int[rawTasks.Count];
        for (int i = 0; i < parentIndices.Length; i++) parentIndices[i] = -1;

        for (int i = 0; i < rawTasks.Count; i++)
        {
            int currentIndent = rawTasks[i].IndentSpaces;
            while (stack.Count > 0 && stack.Peek().indent >= currentIndent)
            {
                stack.Pop();
            }

            if (stack.Count > 0)
            {
                int parentIdx = stack.Peek().taskIndex;
                parentIndices[i] = parentIdx;
                subtaskCounts[parentIdx]++;
                if (rawTasks[i].IsCompleted)
                {
                    completedSubtaskCounts[parentIdx]++;
                }
                indentLevels[i] = stack.Count;
            }
            else
            {
                indentLevels[i] = 0;
            }

            stack.Push((currentIndent, i));
        }

        var items = new List<TaskListHierarchyItem>();
        int inconsistentParentCount = 0;

        for (int i = 0; i < rawTasks.Count; i++)
        {
            var task = rawTasks[i];
            int subCount = subtaskCounts[i];
            int compSubCount = completedSubtaskCounts[i];
            bool hasInconsistency = false;
            string status = "Aligned";

            if (subCount > 0)
            {
                if (task.IsCompleted && compSubCount < subCount)
                {
                    hasInconsistency = true;
                    inconsistentParentCount++;
                    status = $"Inconsistent (Parent marked done, but {subCount - compSubCount} subtasks open)";
                }
                else if (!task.IsCompleted && compSubCount == subCount)
                {
                    hasInconsistency = true;
                    inconsistentParentCount++;
                    status = "Inconsistent (All subtasks completed, parent remains open)";
                }
                else if (task.IsCompleted)
                {
                    status = "Fully Completed Parent";
                }
                else
                {
                    status = $"In Progress Parent ({compSubCount}/{subCount} subtasks done)";
                }
            }
            else
            {
                status = task.IsCompleted ? "Completed Task" : "Open Task";
            }

            items.Add(new TaskListHierarchyItem(
                task.LineNumber,
                task.TaskText,
                indentLevels[i],
                task.IsCompleted,
                subCount,
                compSubCount,
                hasInconsistency,
                status));
        }

        int totalTasks = rawTasks.Count;
        int completedTasks = rawTasks.Count(t => t.IsCompleted);
        int rootTasks = indentLevels.Count(l => l == 0);
        int nestedSubtasks = totalTasks - rootTasks;
        int maxNestingDepth = indentLevels.Max();

        double completionPercentage = Math.Round((double)completedTasks / totalTasks * 100.0, 1);
        double penalty = (inconsistentParentCount * 25.0) + (maxNestingDepth > 3 ? (maxNestingDepth - 3) * 10.0 : 0.0);
        double hierarchyScore = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category;
        if (inconsistentParentCount == 0 && completedTasks == totalTasks)
            category = "All Tasks Completed (100% Monotonic Harmony)";
        else if (inconsistentParentCount == 0)
            category = "Coherent Task Hierarchy (In-Progress)";
        else
            category = $"Hierarchy Inconsistency ({inconsistentParentCount} Parent-Child Discrepanc{(inconsistentParentCount == 1 ? "y" : "ies")})";

        string summary = FormatTaskListHierarchySummary(
            totalTasks, completedTasks, rootTasks, nestedSubtasks, inconsistentParentCount,
            completionPercentage, hierarchyScore, category);

        return new TaskListHierarchyAudit(
            totalTasks,
            completedTasks,
            rootTasks,
            nestedSubtasks,
            inconsistentParentCount,
            maxNestingDepth,
            completionPercentage,
            hierarchyScore,
            items,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown task list hierarchy and progress.
    /// </summary>
    public static string FormatTaskListHierarchySummary(string? markdownText)
        => AuditTaskListHierarchyAndProgress(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of task list progress and hierarchy metrics.
    /// </summary>
    public static string FormatTaskListHierarchySummary(
        int totalTasks, int completedTasks, int rootTasks, int nestedSubtasks, int inconsistentParents,
        double completionPct, double hierarchyScore, string category)
    {
        if (totalTasks == 0)
            return "Task list progress & hierarchy: 0 tasks · Document contains no GFM task lists";

        string tasksLabel = totalTasks == 1 ? "1 task" : $"{totalTasks} tasks";
        var alerts = new List<string>();
        if (inconsistentParents > 0) alerts.Add($"⚠ {inconsistentParents} inconsistent parent-child states");
        if (nestedSubtasks > 0) alerts.Add($"{nestedSubtasks} subtasks");
        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · flat root checklist";

        return $"Task list progress & hierarchy ({completedTasks}/{tasksLabel} [{completionPct:0.#}% done] · {category} · {hierarchyScore:0.#}% hierarchy score){alertSuffix}";
    }

    /// <summary>
    /// Audits markdown table column alignment syntax, header delimiter rows (|:---|:---:|---:|),
    /// outer border pipes, and ragged row column counts across the document.
    /// </summary>
    public static TableColumnAlignmentSyntaxAudit AuditTableColumnAlignmentSyntax(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new TableColumnAlignmentSyntaxAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<TableColumnAlignmentItem>(),
                "No Tables",
                "Markdown table column alignment: 0 tables · Document contains no markdown tables");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        bool inCodeFence = false;
        bool inHtmlComment = false;

        var tableItems = new List<TableColumnAlignmentItem>();
        int totalCols = 0;
        int leftAligned = 0;
        int centerAligned = 0;
        int rightAligned = 0;
        int defaultAligned = 0;
        int totalRaggedRows = 0;
        int missingOuterPipes = 0;

        var delimiterCellRegex = new Regex(@"^\s*:?-{1,}:?\s*$", RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            if (trimmed.StartsWith("<!--") && !trimmed.EndsWith("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;

            if (i > 0 && trimmed.Contains('|') && trimmed.Contains('-'))
            {
                string prevLine = lines[i - 1].Trim();
                if (string.IsNullOrWhiteSpace(prevLine) || prevLine.StartsWith('#')) continue;

                var rawDelimiterCells = line.Split('|');
                int startToken = line.StartsWith("|") || trimmed.StartsWith("|") ? 1 : 0;
                int endToken = line.EndsWith("|") || trimmed.EndsWith("|") ? rawDelimiterCells.Length - 1 : rawDelimiterCells.Length;

                var delimiterCells = new List<string>();
                for (int t = startToken; t < endToken; t++)
                {
                    delimiterCells.Add(rawDelimiterCells[t]);
                }

                if (delimiterCells.Count == 0) continue;

                bool isDelimiter = true;
                var alignments = new List<string>();
                foreach (var cell in delimiterCells)
                {
                    string c = cell.Trim();
                    if (!delimiterCellRegex.IsMatch(c))
                    {
                        isDelimiter = false;
                        break;
                    }

                    bool startsWithColon = c.StartsWith(":");
                    bool endsWithColon = c.EndsWith(":");

                    if (startsWithColon && endsWithColon)
                    {
                        alignments.Add("Center");
                    }
                    else if (startsWithColon)
                    {
                        alignments.Add("Left");
                    }
                    else if (endsWithColon)
                    {
                        alignments.Add("Right");
                    }
                    else
                    {
                        alignments.Add("Default");
                    }
                }

                if (!isDelimiter || alignments.Count == 0) continue;

                int startLine = i;
                int tableCols = alignments.Count;
                totalCols += tableCols;
                foreach (var a in alignments)
                {
                    if (a == "Left") leftAligned++;
                    else if (a == "Center") centerAligned++;
                    else if (a == "Right") rightAligned++;
                    else defaultAligned++;
                }

                bool headerHasOuterPipes = trimmed.StartsWith("|") && trimmed.EndsWith("|");
                if (!headerHasOuterPipes) missingOuterPipes++;

                int rowCount = 0;
                int raggedRowsInTable = 0;

                int r = i + 1;
                while (r < lines.Length)
                {
                    string dataLine = lines[r];
                    string dataTrimmed = dataLine.Trim();
                    if (string.IsNullOrWhiteSpace(dataTrimmed)) break;
                    if (dataTrimmed.StartsWith("```") || dataTrimmed.StartsWith("~~~") || dataTrimmed.StartsWith("#")) break;
                    if (!dataTrimmed.Contains('|')) break;

                    rowCount++;
                    bool rowHasOuter = dataTrimmed.StartsWith("|") && dataTrimmed.EndsWith("|");
                    if (!rowHasOuter) missingOuterPipes++;

                    var rawDataCells = dataLine.Split('|');
                    int ds = dataTrimmed.StartsWith("|") ? 1 : 0;
                    int de = dataTrimmed.EndsWith("|") ? rawDataCells.Length - 1 : rawDataCells.Length;
                    int cellCount = Math.Max(0, de - ds);

                    if (cellCount != tableCols)
                    {
                        raggedRowsInTable++;
                    }

                    r++;
                }

                totalRaggedRows += raggedRowsInTable;
                string patternStr = string.Join("-", alignments.Select(a => a[0]));
                string status = raggedRowsInTable == 0
                    ? $"Standard ({tableCols} cols, {patternStr})"
                    : $"Ragged ({raggedRowsInTable} rows mismatched)";

                tableItems.Add(new TableColumnAlignmentItem(
                    tableItems.Count + 1,
                    startLine,
                    tableCols,
                    rowCount,
                    raggedRowsInTable,
                    headerHasOuterPipes,
                    alignments,
                    patternStr,
                    status));

                i = r - 1;
            }
        }

        if (tableItems.Count == 0)
        {
            return new TableColumnAlignmentSyntaxAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<TableColumnAlignmentItem>(),
                "No Tables",
                "Markdown table column alignment: 0 tables · Document contains no markdown tables");
        }

        double penalty = (totalRaggedRows * 15.0) + (missingOuterPipes * 3.0);
        double consistencyScore = Math.Round(Math.Clamp(100.0 - penalty, 10.0, 100.0), 1);

        string category;
        if (totalRaggedRows == 0 && missingOuterPipes == 0)
            category = "Strict Standard GFM Delimiter Alignment";
        else if (totalRaggedRows == 0)
            category = "Consistent Column Alignment (Missing Optional Outer Border Pipes)";
        else if (consistencyScore >= 70.0)
            category = $"Minor Table Syntax Drift ({totalRaggedRows} Ragged Row{(totalRaggedRows == 1 ? "" : "s")})";
        else
            category = $"High Ragged Table Delimiter Debt ({totalRaggedRows} Mismatched Row{(totalRaggedRows == 1 ? "" : "s")} Detected)";

        string summary = FormatTableColumnAlignmentSummary(
            tableItems.Count, totalCols, leftAligned, centerAligned, rightAligned, defaultAligned,
            totalRaggedRows, missingOuterPipes, consistencyScore, category);

        return new TableColumnAlignmentSyntaxAudit(
            tableItems.Count,
            totalCols,
            leftAligned,
            centerAligned,
            rightAligned,
            defaultAligned,
            totalRaggedRows,
            missingOuterPipes,
            consistencyScore,
            tableItems,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown table column alignment syntax.
    /// </summary>
    public static string FormatTableColumnAlignmentSummary(string? markdownText)
        => AuditTableColumnAlignmentSyntax(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of markdown table column alignment syntax metrics.
    /// </summary>
    public static string FormatTableColumnAlignmentSummary(
        int totalTables, int totalColumns, int leftAligned, int centerAligned, int rightAligned, int defaultAligned,
        int raggedRowCount, int missingPipeCount, double alignmentConsistencyScore, string category)
    {
        if (totalTables == 0)
            return "Markdown table column alignment: 0 tables · Document contains no markdown tables";

        string tableLabel = totalTables == 1 ? "1 table" : $"{totalTables} tables";
        string colLabel = totalColumns == 1 ? "1 column" : $"{totalColumns} columns";

        var alerts = new List<string>();
        if (raggedRowCount > 0) alerts.Add($"⚠ {raggedRowCount} ragged {(raggedRowCount == 1 ? "row" : "rows")}");
        if (missingPipeCount > 0) alerts.Add($"{missingPipeCount} missing outer pipes");
        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% uniform delimiter borders";

        return $"Markdown table column alignment ({tableLabel} [{colLabel}] · {category} · {alignmentConsistencyScore:0.#}% score): " +
               $"{leftAligned} left, {centerAligned} center, {rightAligned} right, {defaultAligned} default{alertSuffix}";
    }

    /// <summary>
    /// Audits markdown footnote reference anchors ([^label]) against footnote definitions ([^label]: text),
    /// detecting dangling references (missing definitions), orphaned definitions (unreferenced definitions),
    /// duplicate definition tags, and scoring document reference integrity with code fence immunity.
    /// </summary>
    public static FootnoteReferencePairingAudit AuditFootnoteReferencePairing(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new FootnoteReferencePairingAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<FootnoteReferencePairingItem>(),
                "Zero Footnotes (Clean Baseline)",
                "Footnote reference pairing: 0 footnotes · Document contains no footnote references or definitions");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inFence = false;
        string fenceMarker = "";

        var refCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var defCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var canonicalCasing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var defRegex = new System.Text.RegularExpressions.Regex(@"^\s*\[\^([^\]]+)\]:\s*(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
        var refRegex = new System.Text.RegularExpressions.Regex(@"\[\^([^\]]+)\](?!:)", System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();

            if (!inFence)
            {
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = true;
                    fenceMarker = trimmed.Substring(0, 3);
                    continue;
                }
            }
            else
            {
                if (trimmed.StartsWith(fenceMarker))
                {
                    inFence = false;
                }
                continue;
            }

            string cleanLine = rawLine;
            while (cleanLine.TrimStart().StartsWith(">"))
            {
                int gtIdx = cleanLine.IndexOf('>');
                cleanLine = cleanLine.Substring(gtIdx + 1);
            }

            var defMatch = defRegex.Match(cleanLine);
            if (defMatch.Success)
            {
                string id = defMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    defCounts[id] = defCounts.TryGetValue(id, out int dc) ? dc + 1 : 1;
                    if (!canonicalCasing.ContainsKey(id)) canonicalCasing[id] = id;
                }
            }
            else
            {
                var refMatches = refRegex.Matches(cleanLine);
                foreach (System.Text.RegularExpressions.Match rm in refMatches)
                {
                    string id = rm.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(id))
                    {
                        refCounts[id] = refCounts.TryGetValue(id, out int rc) ? rc + 1 : 1;
                        if (!canonicalCasing.ContainsKey(id)) canonicalCasing[id] = id;
                    }
                }
            }
        }

        int totalRefs = refCounts.Values.Sum();
        int totalDefs = defCounts.Values.Sum();
        int uniqueRefs = refCounts.Count;
        int uniqueDefs = defCounts.Count;

        if (totalRefs == 0 && totalDefs == 0)
        {
            return new FootnoteReferencePairingAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<FootnoteReferencePairingItem>(),
                "Zero Footnotes (Clean Baseline)",
                "Footnote reference pairing: 0 footnotes · Document contains no footnote references or definitions");
        }

        var allIds = new HashSet<string>(refCounts.Keys.Concat(defCounts.Keys), StringComparer.OrdinalIgnoreCase);
        var items = new List<FootnoteReferencePairingItem>();

        int danglingCount = 0;
        int orphanCount = 0;
        int duplicateDefs = 0;
        int matchedPairs = 0;

        foreach (var id in allIds.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            int rCount = refCounts.TryGetValue(id, out int rc) ? rc : 0;
            int dCount = defCounts.TryGetValue(id, out int dc) ? dc : 0;
            bool isDangling = rCount > 0 && dCount == 0;
            bool isOrphan = dCount > 0 && rCount == 0;
            bool hasDupes = dCount > 1;

            if (isDangling) danglingCount++;
            if (isOrphan) orphanCount++;
            if (hasDupes) duplicateDefs += (dCount - 1);
            if (rCount > 0 && dCount > 0) matchedPairs++;

            string status = isDangling ? "Dangling (Missing Definition)"
                : isOrphan ? "Orphan (Unreferenced Definition)"
                : hasDupes ? $"Duplicate Definitions ({dCount})"
                : $"Matched ({rCount} ref, {dCount} def)";

            string displayId = canonicalCasing.TryGetValue(id, out var c) ? c : id;
            items.Add(new FootnoteReferencePairingItem(displayId, rCount, dCount, isDangling, isOrphan, hasDupes, status));
        }

        double score = 100.0;
        score -= danglingCount * 25.0;
        score -= duplicateDefs * 15.0;
        score -= orphanCount * 10.0;
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category;
        if (danglingCount == 0 && orphanCount == 0 && duplicateDefs == 0)
            category = "Complete Bi-Directional Footnote Pairing";
        else if (danglingCount > 0 && orphanCount == 0)
            category = $"Dangling Footnote References Present ({danglingCount} Unresolved)";
        else if (orphanCount > 0 && danglingCount == 0)
            category = $"Orphan Footnote Definitions Present ({orphanCount} Unreferenced)";
        else if (duplicateDefs > 0 && danglingCount == 0 && orphanCount == 0)
            category = $"Duplicate Footnote Definitions Present ({duplicateDefs} Redundant)";
        else
            category = "Mixed Footnote Pairing Inconsistencies";

        string summary = FormatFootnoteReferencePairingSummary(
            totalRefs, uniqueRefs, totalDefs, uniqueDefs, danglingCount, orphanCount, duplicateDefs, matchedPairs, score, category);

        return new FootnoteReferencePairingAudit(
            totalRefs, uniqueRefs, totalDefs, uniqueDefs, danglingCount, orphanCount, duplicateDefs,
            matchedPairs, score, items, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of footnote reference pairing and dangling definitions from markdown text.
    /// </summary>
    public static string FormatFootnoteReferencePairingSummary(string? markdownText)
        => AuditFootnoteReferencePairing(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of footnote reference pairing metrics.
    /// </summary>
    public static string FormatFootnoteReferencePairingSummary(
        int totalRefs, int uniqueRefs, int totalDefs, int uniqueDefs, int dangling, int orphans, int duplicates,
        int matched, double score, string category)
    {
        if (totalRefs == 0 && totalDefs == 0)
            return "Footnote reference pairing: 0 footnotes · Document contains no footnote references or definitions";

        var alerts = new List<string>();
        if (dangling > 0) alerts.Add($"⚠ {dangling} dangling {(dangling == 1 ? "ref" : "refs")}");
        if (orphans > 0) alerts.Add($"{orphans} orphan {(orphans == 1 ? "def" : "defs")}");
        if (duplicates > 0) alerts.Add($"{duplicates} duplicate {(duplicates == 1 ? "def" : "defs")}");
        string alertSuffix = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% paired bi-directional anchors";

        return $"Footnote reference pairing ({totalRefs} refs [{uniqueRefs} unique], {totalDefs} defs [{uniqueDefs} unique] · {category} · {score:0.#}% score): " +
               $"{matched} matched {(matched == 1 ? "pair" : "pairs")}{alertSuffix}";
    }

    /// <summary>
    /// Audits document image embeds (![alt](src) and &lt;img ...&gt;) for alt-text quality, meaningful descriptions,
    /// empty caption violations, generic placeholder descriptors, and WCAG 2.1 Non-Text Content accessibility compliance,
    /// with code-fence immunity.
    /// </summary>
    public static DocumentImageAltTextAccessibilityAudit AuditDocumentImageAltTextAndAccessibility(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new DocumentImageAltTextAccessibilityAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<DocumentImageAltItem>(),
                "Zero Images (Accessible Baseline)",
                "Image alt-text WCAG audit: 0 images · Document contains no image embeds");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inFence = false;
        string fenceMarker = "";

        var items = new List<DocumentImageAltItem>();
        int lineNumber = 0;

        var mdImgRegex = new System.Text.RegularExpressions.Regex(@"!\[(?<alt>[^\]]*)\]\((?<src>[^)]+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);
        var htmlImgRegex = new System.Text.RegularExpressions.Regex(@"<img\b(?<attrs>[^>]*)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        var altAttrRegex = new System.Text.RegularExpressions.Regex(@"\balt\s*=\s*[""'](?<alt>[^""']*)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        var srcAttrRegex = new System.Text.RegularExpressions.Regex(@"\bsrc\s*=\s*[""'](?<src>[^""']*)[""']", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        var genericNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image", "photo", "pic", "picture", "screenshot", "screen shot", "diagram",
            "figure", "graphic", "untitled", "img", "placeholder", "icon", "drawing",
            "illustration", "banner", "thumbnail", "preview", "asset"
        };

        var redundantPrefixes = new[]
        {
            "image of", "photo of", "picture of", "graphic of", "screenshot of",
            "icon of", "drawing of", "illustration of", "banner of"
        };

        foreach (var rawLine in lines)
        {
            lineNumber++;
            string trimmed = rawLine.Trim();

            if (!inFence)
            {
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inFence = true;
                    fenceMarker = trimmed.Substring(0, 3);
                    continue;
                }
            }
            else
            {
                if (trimmed.StartsWith(fenceMarker))
                {
                    inFence = false;
                }
                continue;
            }

            string cleanLine = rawLine;
            while (cleanLine.TrimStart().StartsWith(">"))
            {
                cleanLine = cleanLine.TrimStart().Substring(1);
            }

            // Outside code fences, strip inline backtick code spans before matching images
            string strippedLine = System.Text.RegularExpressions.Regex.Replace(cleanLine, @"`[^`]*`", "");

            // 1. Markdown images: ![alt](src)
            var mdMatches = mdImgRegex.Matches(strippedLine);
            foreach (System.Text.RegularExpressions.Match match in mdMatches)
            {
                string rawAlt = match.Groups["alt"].Value;
                string rawSrc = match.Groups["src"].Value.Trim();

                // Strip optional title from src e.g. "path/to/img.png" "Title"
                string cleanSrc = rawSrc;
                int quoteIdx = cleanSrc.IndexOfAny(new[] { '"', '\'' });
                if (quoteIdx > 0)
                {
                    cleanSrc = cleanSrc.Substring(0, quoteIdx).Trim();
                }

                items.Add(EvaluateImageAltItem(lineNumber, rawAlt, cleanSrc, genericNames, redundantPrefixes));
            }

            // 2. HTML images: <img ... />
            var htmlMatches = htmlImgRegex.Matches(strippedLine);
            foreach (System.Text.RegularExpressions.Match match in htmlMatches)
            {
                string attrs = match.Groups["attrs"].Value;
                var altMatch = altAttrRegex.Match(attrs);
                var srcMatch = srcAttrRegex.Match(attrs);

                string altText = altMatch.Success ? altMatch.Groups["alt"].Value : "";
                string srcText = srcMatch.Success ? srcMatch.Groups["src"].Value.Trim() : "";

                items.Add(EvaluateImageAltItem(lineNumber, altText, srcText, genericNames, redundantPrefixes));
            }
        }

        if (items.Count == 0)
        {
            return new DocumentImageAltTextAccessibilityAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<DocumentImageAltItem>(),
                "Zero Images (Accessible Baseline)",
                "Image alt-text WCAG audit: 0 images · Document contains no image embeds");
        }

        int totalImages = items.Count;
        int descriptiveCount = items.Count(i => i.QualityTier == "Descriptive Alt Text");
        int emptyCount = items.Count(i => i.IsEmpty);
        int genericCount = items.Count(i => i.IsGenericPlaceholder);
        int redundantPrefixCount = items.Count(i => i.HasRedundantPrefix);
        int overlyLongCount = items.Count(i => i.IsOverlyLong);
        int dataUriCount = items.Count(i => i.IsDataUri);

        // Score formula: rewards descriptive descriptions, penalizes empty alt, generic placeholders, and redundant prefixes
        double rawScore = 100.0 * (descriptiveCount * 1.0 + (items.Count - descriptiveCount - emptyCount - genericCount - redundantPrefixCount) * 0.8
            + redundantPrefixCount * 0.5 - emptyCount * 1.0 - genericCount * 0.8) / totalImages;
        double complianceScore = Math.Max(0.0, Math.Min(100.0, Math.Round(rawScore, 1)));

        string category = complianceScore switch
        {
            >= 95.0 => "Full WCAG 2.1 Accessibility Compliance",
            >= 80.0 => "Good Alt-Text Accessibility",
            >= 60.0 => "Moderate Accessibility (Improve Descriptions)",
            _ => "Poor Accessibility (Missing or Generic Alt-Text)"
        };

        string summary = FormatImageAltTextAccessibilitySummary(
            totalImages, descriptiveCount, emptyCount, genericCount, redundantPrefixCount,
            overlyLongCount, dataUriCount, complianceScore, category);

        return new DocumentImageAltTextAccessibilityAudit(
            totalImages, descriptiveCount, emptyCount, genericCount, redundantPrefixCount,
            overlyLongCount, dataUriCount, complianceScore, items, category, summary);
    }

    private static DocumentImageAltItem EvaluateImageAltItem(
        int lineNumber,
        string rawAlt,
        string cleanSrc,
        HashSet<string> genericNames,
        string[] redundantPrefixes)
    {
        string alt = rawAlt.Trim();
        bool isEmpty = string.IsNullOrWhiteSpace(alt);
        bool isDataUri = cleanSrc.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

        string fileName = "";
        try
        {
            fileName = Path.GetFileName(cleanSrc);
        }
        catch
        {
            fileName = cleanSrc;
        }

        string fileNameWithoutExt = "";
        try
        {
            fileNameWithoutExt = Path.GetFileNameWithoutExtension(cleanSrc);
        }
        catch
        {
            fileNameWithoutExt = cleanSrc;
        }

        bool isGeneric = false;
        if (!isEmpty)
        {
            if (genericNames.Contains(alt))
            {
                isGeneric = true;
            }
            else if (!string.IsNullOrEmpty(fileName) && string.Equals(alt, fileName, StringComparison.OrdinalIgnoreCase))
            {
                isGeneric = true;
            }
            else if (!string.IsNullOrEmpty(fileNameWithoutExt) && string.Equals(alt, fileNameWithoutExt, StringComparison.OrdinalIgnoreCase))
            {
                isGeneric = true;
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(alt, @"^(?:image|img|screenshot|pic|figure)[_\-\s0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                isGeneric = true;
            }
        }

        bool hasRedundantPrefix = false;
        if (!isEmpty)
        {
            foreach (var prefix in redundantPrefixes)
            {
                if (alt.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    hasRedundantPrefix = true;
                    break;
                }
            }
        }

        bool isOverlyLong = alt.Length > 150;

        string qualityTier;
        string status;

        if (isEmpty)
        {
            qualityTier = "Missing / Empty Alt Text";
            status = "⚠ Missing alt-text violates WCAG 1.1.1 Non-Text Content";
        }
        else if (isGeneric)
        {
            qualityTier = "Generic Placeholder Alt Text";
            status = "⚠ Placeholder alt-text provides non-descriptive context";
        }
        else if (hasRedundantPrefix)
        {
            qualityTier = "Redundant Prefix Phrasing";
            status = "⚠ Screen readers announce image role automatically; drop 'image of'";
        }
        else if (isOverlyLong)
        {
            qualityTier = "Overly Long Alt Text";
            status = "ℹ Alt text exceeds 150 chars; consider moving detailed narrative to caption";
        }
        else if (alt.Length >= 8)
        {
            qualityTier = "Descriptive Alt Text";
            status = "✓ Meaningful, descriptive accessible alternative text";
        }
        else
        {
            qualityTier = "Concise Alt Text";
            status = "✓ Concise accessible description";
        }

        return new DocumentImageAltItem(
            lineNumber,
            rawAlt,
            cleanSrc,
            isEmpty,
            isGeneric,
            hasRedundantPrefix,
            isOverlyLong,
            isDataUri,
            qualityTier,
            status);
    }

    /// <summary>
    /// Formats a human-readable summary of image alt-text accessibility from markdown text.
    /// </summary>
    public static string FormatImageAltTextAccessibilitySummary(string? markdownText)
        => AuditDocumentImageAltTextAndAccessibility(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of image alt-text WCAG accessibility metrics.
    /// </summary>
    public static string FormatImageAltTextAccessibilitySummary(
        int totalImages, int descriptive, int empty, int generic, int redundantPrefix,
        int overlyLong, int dataUri, double score, string category)
    {
        if (totalImages == 0)
            return "Image alt-text WCAG audit: 0 images · Document contains no image embeds";

        string imageLabel = totalImages == 1 ? "1 image" : $"{totalImages} images";
        var alerts = new List<string>();
        if (empty > 0) alerts.Add($"⚠ {empty} empty");
        if (generic > 0) alerts.Add($"{generic} generic");
        if (redundantPrefix > 0) alerts.Add($"{redundantPrefix} redundant prefix");
        if (overlyLong > 0) alerts.Add($"{overlyLong} overly long (>150 chars)");
        if (dataUri > 0) alerts.Add($"{dataUri} data URI");

        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% descriptive alt text";
        return $"Image alt-text WCAG audit ({imageLabel} · {category} · {score:0.#}% score): {descriptive} descriptive{details}";
    }

    /// <summary>
    /// Audits markdown strikethrough (~~text~~) and highlight (==text==) syntax balance,
    /// detecting unclosed spans, inner whitespace errors, empty spans, and scoring formatting balance.
    /// </summary>
    public static StrikethroughHighlightFormattingAudit AuditStrikethroughAndHighlightFormatting(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new StrikethroughHighlightFormattingAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<StrikethroughHighlightSpanItem>(),
                "Zero Styled Spans (Clean Baseline)",
                "Markdown strikethrough & highlight audit: 0 styled spans · Clean baseline");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inFence = false;
        string fenceMarker = "";

        var items = new List<StrikethroughHighlightSpanItem>();
        int strikethroughCount = 0;
        int highlightCount = 0;
        int balancedCount = 0;
        int unclosedCount = 0;
        int whitespaceErrorCount = 0;
        int emptySpanCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (!inFence && (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")))
            {
                inFence = true;
                fenceMarker = trimmed[..3];
                continue;
            }
            if (inFence && trimmed.StartsWith(fenceMarker))
            {
                inFence = false;
                fenceMarker = "";
                continue;
            }
            if (inFence) continue;

            // Strip inline code blocks (`...`)
            string masked = System.Text.RegularExpressions.Regex.Replace(line, @"`+[^`\r\n]*`+", m => new string(' ', m.Length));

            // Process Strikethrough (~~)
            ProcessDelimiters(masked, line, i + 1, "~~", "Strikethrough", items, ref strikethroughCount, ref balancedCount, ref unclosedCount, ref whitespaceErrorCount, ref emptySpanCount);

            // Process Highlight (==)
            ProcessDelimiters(masked, line, i + 1, "==", "Highlight", items, ref highlightCount, ref balancedCount, ref unclosedCount, ref whitespaceErrorCount, ref emptySpanCount);
        }

        int totalSpans = items.Count;
        if (totalSpans == 0)
        {
            return new StrikethroughHighlightFormattingAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<StrikethroughHighlightSpanItem>(),
                "Zero Styled Spans (Clean Baseline)",
                "Markdown strikethrough & highlight audit: 0 styled spans · Clean baseline");
        }

        double score = Math.Round((double)balancedCount / totalSpans * 100.0, 1);
        string category = score switch
        {
            >= 95.0 => "Flawless Inline Styling Balance",
            >= 75.0 => "High Formatting Fidelity (Minor Spans/Whitespace)",
            >= 50.0 => "Moderate Inline Delimiter Asymmetry",
            _ => "Severe Unclosed Delimiters / Broken Formatting"
        };

        string summary = FormatStrikethroughHighlightSummary(
            totalSpans, strikethroughCount, highlightCount, balancedCount,
            unclosedCount, whitespaceErrorCount, emptySpanCount, score, category);

        return new StrikethroughHighlightFormattingAudit(
            totalSpans, strikethroughCount, highlightCount, balancedCount,
            unclosedCount, whitespaceErrorCount, emptySpanCount, score,
            items, category, summary);
    }

    private static void ProcessDelimiters(
        string masked, string rawLine, int lineNumber, string delim, string typeName,
        List<StrikethroughHighlightSpanItem> items,
        ref int typeCount, ref int balancedCount, ref int unclosedCount,
        ref int whitespaceErrorCount, ref int emptySpanCount)
    {
        int cursor = 0;
        int dLen = delim.Length;

        while (cursor < masked.Length)
        {
            int start = masked.IndexOf(delim, cursor, StringComparison.Ordinal);
            if (start == -1) break;

            typeCount++;

            int next = masked.IndexOf(delim, start + dLen, StringComparison.Ordinal);
            if (next == -1)
            {
                unclosedCount++;
                string snippet = rawLine.Substring(start);
                items.Add(new StrikethroughHighlightSpanItem(
                    lineNumber, typeName, snippet, snippet, false, false, false, "Unclosed Delimiter (Missing Ending Tag)"));
                break;
            }

            int innerStart = start + dLen;
            int innerLen = next - innerStart;
            string innerText = rawLine.Substring(innerStart, innerLen);
            string rawSpan = rawLine.Substring(start, next + dLen - start);

            bool isEmpty = string.IsNullOrEmpty(innerText);
            bool hasLeadingWs = !isEmpty && char.IsWhiteSpace(innerText[0]);
            bool hasTrailingWs = !isEmpty && char.IsWhiteSpace(innerText[^1]);
            bool hasWhitespaceError = hasLeadingWs || hasTrailingWs;

            if (isEmpty)
            {
                emptySpanCount++;
                items.Add(new StrikethroughHighlightSpanItem(
                    lineNumber, typeName, rawSpan, innerText, true, false, true, "Empty Delimiter Span (Zero Text)"));
            }
            else if (hasWhitespaceError)
            {
                whitespaceErrorCount++;
                string wsDetail = (hasLeadingWs && hasTrailingWs) ? "Leading & Trailing Whitespace"
                    : hasLeadingWs ? "Leading Whitespace Error" : "Trailing Whitespace Error";
                items.Add(new StrikethroughHighlightSpanItem(
                    lineNumber, typeName, rawSpan, innerText, true, true, false, $"Invalid Internal Whitespace ({wsDetail})"));
            }
            else
            {
                balancedCount++;
                items.Add(new StrikethroughHighlightSpanItem(
                    lineNumber, typeName, rawSpan, innerText, true, false, false, "Balanced Formatting Span"));
            }

            cursor = next + dLen;
        }
    }

    /// <summary>
    /// Formats a human-readable summary of strikethrough and highlight formatting syntax from markdown text.
    /// </summary>
    public static string FormatStrikethroughHighlightSummary(string? markdownText)
        => AuditStrikethroughAndHighlightFormatting(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of strikethrough and highlight formatting metrics.
    /// </summary>
    public static string FormatStrikethroughHighlightSummary(
        int totalSpans, int strikethrough, int highlight, int balanced, int unclosed,
        int whitespaceErrors, int emptySpans, double score, string category)
    {
        if (totalSpans == 0)
            return "Markdown strikethrough & highlight audit: 0 styled spans · Clean baseline";

        string spanLabel = totalSpans == 1 ? "1 span" : $"{totalSpans} spans";
        var alerts = new List<string>();
        if (unclosed > 0) alerts.Add($"⚠ {unclosed} unclosed");
        if (whitespaceErrors > 0) alerts.Add($"{whitespaceErrors} whitespace {(whitespaceErrors == 1 ? "error" : "errors")}");
        if (emptySpans > 0) alerts.Add($"{emptySpans} empty");

        string typeDist = $"{strikethrough} ~~strikethrough~~, {highlight} ==highlight==";
        string details = alerts.Count > 0 ? $" · {string.Join(", ", alerts)}" : " · 100% balanced formatting";
        return $"Markdown strikethrough & highlight audit ({spanLabel} [{typeDist}] · {category} · {score:0.#}% score): {balanced} balanced{details}";
    }

    /// <summary>
    /// Audits markdown blockquote nesting depth (>), GitHub-style alert callouts (> [!NOTE], > [!TIP], etc.),
    /// attribution citations (> -- Author), and structural ergonomics.
    /// </summary>
    public static BlockquoteNestingAndCalloutAudit AuditBlockquoteNestingAndCallouts(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new BlockquoteNestingAndCalloutAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<BlockquoteCalloutItem>(), "Optimal",
                FormatBlockquoteNestingSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 100.0, "Optimal"));
        }

        var lines = markdownText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var items = new List<BlockquoteCalloutItem>();
        bool inFence = false;
        int totalBlocks = 0;
        bool inQuoteBlock = false;
        int maxDepth = 0;
        int noteAlerts = 0, tipAlerts = 0, importantAlerts = 0, warningAlerts = 0, cautionAlerts = 0;
        int citations = 0;
        int nestedDeep = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.TrimStart();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                inFence = !inFence;
                if (inQuoteBlock) inQuoteBlock = false;
                continue;
            }

            if (inFence) continue;

            if (trimmed.StartsWith('>'))
            {
                if (!inQuoteBlock)
                {
                    inQuoteBlock = true;
                    totalBlocks++;
                }

                int depth = 0;
                int idx = 0;
                while (idx < trimmed.Length)
                {
                    if (trimmed[idx] == '>')
                    {
                        depth++;
                        idx++;
                    }
                    else if (char.IsWhiteSpace(trimmed[idx]))
                    {
                        idx++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (depth > maxDepth) maxDepth = depth;
                if (depth > 2) nestedDeep++;

                string inner = idx < trimmed.Length ? trimmed[idx..].Trim() : string.Empty;
                bool isAlert = false;
                string alertType = "None";
                bool isCitation = false;
                bool isEmpty = string.IsNullOrWhiteSpace(inner);

                if (!isEmpty)
                {
                    if (inner.StartsWith("[!NOTE]", StringComparison.OrdinalIgnoreCase))
                    {
                        isAlert = true;
                        alertType = "NOTE";
                        noteAlerts++;
                    }
                    else if (inner.StartsWith("[!TIP]", StringComparison.OrdinalIgnoreCase))
                    {
                        isAlert = true;
                        alertType = "TIP";
                        tipAlerts++;
                    }
                    else if (inner.StartsWith("[!IMPORTANT]", StringComparison.OrdinalIgnoreCase))
                    {
                        isAlert = true;
                        alertType = "IMPORTANT";
                        importantAlerts++;
                    }
                    else if (inner.StartsWith("[!WARNING]", StringComparison.OrdinalIgnoreCase))
                    {
                        isAlert = true;
                        alertType = "WARNING";
                        warningAlerts++;
                    }
                    else if (inner.StartsWith("[!CAUTION]", StringComparison.OrdinalIgnoreCase))
                    {
                        isAlert = true;
                        alertType = "CAUTION";
                        cautionAlerts++;
                    }
                    else if (inner.StartsWith("--") || inner.StartsWith("—") || inner.StartsWith("–"))
                    {
                        isCitation = true;
                        citations++;
                    }
                }

                string status = isAlert ? $"Alert ({alertType})" :
                                isCitation ? "Attributed Citation" :
                                isEmpty ? "Empty Quote Separator" :
                                depth > 2 ? $"Deep Nesting (Level {depth})" :
                                $"Quote (Level {depth})";

                items.Add(new BlockquoteCalloutItem(
                    i + 1, depth, inner, isAlert, alertType, isCitation, isEmpty, status));
            }
            else
            {
                if (inQuoteBlock && !string.IsNullOrWhiteSpace(trimmed))
                {
                    inQuoteBlock = false;
                }
            }
        }

        int totalQuoteLines = items.Count;
        double penalty = (nestedDeep * 6.0);
        if (maxDepth > 4) penalty += (maxDepth - 4) * 10.0;
        double score = totalQuoteLines == 0 ? 100.0 : Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Optimal",
            >= 75.0 => "Balanced",
            >= 60.0 => "Moderate",
            >= 40.0 => "Complex",
            _ => "Excessive Nesting"
        };

        string summary = FormatBlockquoteNestingSummary(
            totalQuoteLines, totalBlocks, maxDepth, noteAlerts, tipAlerts, importantAlerts,
            warningAlerts, cautionAlerts, citations, nestedDeep, score, category);

        return new BlockquoteNestingAndCalloutAudit(
            totalQuoteLines, totalBlocks, maxDepth, noteAlerts, tipAlerts, importantAlerts,
            warningAlerts, cautionAlerts, citations, nestedDeep, score, items, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of blockquote nesting and callouts from markdown text.
    /// </summary>
    public static string FormatBlockquoteNestingSummary(string? markdownText)
        => AuditBlockquoteNestingAndCallouts(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of blockquote nesting and callout metrics.
    /// </summary>
    public static string FormatBlockquoteNestingSummary(
        int totalQuoteLines, int totalBlocks, int maxDepth, int noteAlerts, int tipAlerts,
        int importantAlerts, int warningAlerts, int cautionAlerts, int citations,
        int nestedDeep, double score, string category)
    {
        if (totalQuoteLines == 0)
            return "Markdown blockquote & callout audit: 0 quote lines · Clean baseline";

        string lineLabel = totalQuoteLines == 1 ? "1 line" : $"{totalQuoteLines} lines";
        string blockLabel = totalBlocks == 1 ? "1 block" : $"{totalBlocks} blocks";
        int totalAlerts = noteAlerts + tipAlerts + importantAlerts + warningAlerts + cautionAlerts;

        var parts = new List<string>();
        if (totalAlerts > 0)
        {
            var alertTypes = new List<string>();
            if (noteAlerts > 0) alertTypes.Add($"{noteAlerts} Note");
            if (tipAlerts > 0) alertTypes.Add($"{tipAlerts} Tip");
            if (importantAlerts > 0) alertTypes.Add($"{importantAlerts} Important");
            if (warningAlerts > 0) alertTypes.Add($"{warningAlerts} Warning");
            if (cautionAlerts > 0) alertTypes.Add($"{cautionAlerts} Caution");
            parts.Add($"{totalAlerts} alert {(totalAlerts == 1 ? "callout" : "callouts")} [{string.Join(", ", alertTypes)}]");
        }
        if (citations > 0) parts.Add($"{citations} {(citations == 1 ? "citation" : "citations")}");
        if (nestedDeep > 0) parts.Add($"⚠ {nestedDeep} deep (depth > 2)");

        string details = parts.Count > 0 ? $" · {string.Join(", ", parts)}" : "";
        string prefix = $"Markdown blockquote & callout audit ({lineLabel} in {blockLabel} · Max depth {maxDepth} · {category} · {score:0.#}% score)";
        return details.Length > 0 ? $"{prefix}:{details}" : $"{prefix} · Structured quotes";
    }

    /// <summary>
    /// Audits markdown text for definition lists (Term\n: Definition), verifying term separation,
    /// colon spacing compliance, multiline continuation indentation, and orphan term detection.
    /// Excludes fenced code blocks and frontmatter.
    /// </summary>
    public static DefinitionListContinuityAudit AuditDefinitionListContinuityAndSpacing(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new DefinitionListContinuityAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<DefinitionListContinuityItem>(),
                "Clean Baseline",
                "Markdown definition list audit: 0 terms · Document contains no definition lists");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var maskedLines = new string[lines.Length];
        bool inFence = false;
        string fenceMarker = string.Empty;
        bool inFrontmatter = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (i == 0 && (trimmed == "---" || trimmed == "+++"))
            {
                inFrontmatter = true;
                maskedLines[i] = string.Empty;
                continue;
            }

            if (inFrontmatter)
            {
                if (trimmed == "---" || trimmed == "+++") inFrontmatter = false;
                maskedLines[i] = string.Empty;
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = tag;
                }
                else if (tag == fenceMarker || trimmed == ":::")
                {
                    inFence = false;
                    fenceMarker = string.Empty;
                }
                maskedLines[i] = string.Empty;
                continue;
            }

            if (inFence)
            {
                maskedLines[i] = string.Empty;
                continue;
            }

            maskedLines[i] = line;
        }

        var items = new List<DefinitionListContinuityItem>();
        int totalTerms = 0;
        int totalDefinitions = 0;
        int orphanTerms = 0;
        int multilineDefs = 0;
        int spacingIssues = 0;

        for (int i = 0; i < maskedLines.Length; i++)
        {
            var line = maskedLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var trimmed = line.Trim();
            // Check if this line is a definition line (: def or ~ def)
            bool isDef = (trimmed.StartsWith(":") || trimmed.StartsWith("~")) &&
                         !trimmed.StartsWith(":::") &&
                         !Regex.IsMatch(trimmed, @"^\[\^?[^\]]+\]:");

            if (isDef)
            {
                // Orphan definition with no term above
                spacingIssues++;
                continue;
            }

            // Check if next non-empty line is a definition line
            int nextNonEmpty = -1;
            for (int k = i + 1; k < maskedLines.Length; k++)
            {
                if (!string.IsNullOrWhiteSpace(maskedLines[k]))
                {
                    nextNonEmpty = k;
                    break;
                }
            }

            if (nextNonEmpty != -1)
            {
                var nextTrimmed = maskedLines[nextNonEmpty].Trim();
                bool nextIsDef = (nextTrimmed.StartsWith(":") || nextTrimmed.StartsWith("~")) &&
                                 !nextTrimmed.StartsWith(":::") &&
                                 !Regex.IsMatch(nextTrimmed, @"^\[\^?[^\]]+\]:");

                if (nextIsDef)
                {
                    // Line i is a Term!
                    totalTerms++;
                    string termText = trimmed;
                    int termLineNumber = i + 1;

                    bool hasPrecedingBlank = (i == 0) || string.IsNullOrWhiteSpace(maskedLines[i - 1]) ||
                                             maskedLines[i - 1].Trim().StartsWith("#");

                    if (!hasPrecedingBlank)
                    {
                        spacingIssues++;
                    }

                    int defCount = 0;
                    bool hasValidContinuation = true;
                    int cur = nextNonEmpty;

                    while (cur < maskedLines.Length)
                    {
                        var curLine = maskedLines[cur];
                        if (string.IsNullOrWhiteSpace(curLine))
                        {
                            // Check if next line continues the definition list
                            int peek = cur + 1;
                            while (peek < maskedLines.Length && string.IsNullOrWhiteSpace(maskedLines[peek])) peek++;
                            if (peek < maskedLines.Length)
                            {
                                var peekTrim = maskedLines[peek].Trim();
                                if ((peekTrim.StartsWith(":") || peekTrim.StartsWith("~")) && !peekTrim.StartsWith(":::"))
                                {
                                    cur = peek;
                                    continue;
                                }
                            }
                            break;
                        }

                        var curTrim = curLine.Trim();
                        if ((curTrim.StartsWith(":") || curTrim.StartsWith("~")) && !curTrim.StartsWith(":::"))
                        {
                            defCount++;
                            totalDefinitions++;

                            // Check space after colon/tilde: e.g. ": text"
                            if (curTrim.Length > 1 && !char.IsWhiteSpace(curTrim[1]))
                            {
                                spacingIssues++;
                            }

                            // Check subsequent lines for indentation continuation
                            int cont = cur + 1;
                            while (cont < maskedLines.Length && !string.IsNullOrWhiteSpace(maskedLines[cont]))
                            {
                                var contLine = maskedLines[cont];
                                var contTrim = contLine.Trim();
                                if ((contTrim.StartsWith(":") || contTrim.StartsWith("~")) && !contTrim.StartsWith(":::"))
                                    break;

                                int leadingSpaces = contLine.TakeWhile(c => c == ' ' || c == '\t').Count();
                                bool isIndented = leadingSpaces >= 2 || contLine.StartsWith("\t");
                                if (isIndented)
                                {
                                    multilineDefs++;
                                }
                                else
                                {
                                    hasValidContinuation = false;
                                    spacingIssues++;
                                }
                                cont++;
                            }
                            cur = cont;
                        }
                        else
                        {
                            break;
                        }
                    }

                    string status = hasPrecedingBlank && hasValidContinuation
                        ? $"✓ {defCount} {(defCount == 1 ? "definition" : "definitions")}"
                        : $"⚠ Spacing/continuation alert ({defCount} defs)";

                    items.Add(new DefinitionListContinuityItem(
                        termLineNumber, termText, defCount, hasPrecedingBlank, hasValidContinuation, status));

                    i = cur - 1;
                }
            }
        }

        if (totalTerms == 0)
        {
            return new DefinitionListContinuityAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<DefinitionListContinuityItem>(),
                "Clean Baseline",
                "Markdown definition list audit: 0 terms · Document contains no definition lists");
        }

        double penalty = (spacingIssues * 8.0) + (orphanTerms * 15.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Optimal",
            >= 75.0 => "Consistent",
            >= 60.0 => "Fair",
            >= 40.0 => "Irregular",
            _ => "Fragmented"
        };

        string summary = FormatDefinitionListContinuitySummary(
            totalTerms, totalDefinitions, orphanTerms, multilineDefs, spacingIssues, score, category);

        return new DefinitionListContinuityAudit(
            totalTerms, totalDefinitions, orphanTerms, multilineDefs, spacingIssues,
            score, items, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of definition list continuity from markdown text.
    /// </summary>
    public static string FormatDefinitionListContinuitySummary(string? markdownText)
        => AuditDefinitionListContinuityAndSpacing(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of definition list continuity metrics.
    /// </summary>
    public static string FormatDefinitionListContinuitySummary(
        int totalTerms, int totalDefinitions, int orphanTerms, int multilineDefs,
        int spacingIssues, double score, string category)
    {
        if (totalTerms == 0)
            return "Markdown definition list audit: 0 terms · Document contains no definition lists";

        string termStr = totalTerms == 1 ? "1 term" : $"{totalTerms} terms";
        string defStr = totalDefinitions == 1 ? "1 definition" : $"{totalDefinitions} definitions";

        var parts = new List<string>();
        if (multilineDefs > 0) parts.Add($"{multilineDefs} multiline");
        if (spacingIssues > 0) parts.Add($"⚠ {spacingIssues} spacing {(spacingIssues == 1 ? "alert" : "alerts")}");
        if (orphanTerms > 0) parts.Add($"⚠ {orphanTerms} orphan");

        string details = parts.Count > 0 ? $" · {string.Join(", ", parts)}" : "";
        string prefix = $"Markdown definition list audit ({termStr}, {defStr} · {category} · {score:0.#}% score)";
        return details.Length > 0 ? $"{prefix}:{details}" : $"{prefix} · Structured definitions";
    }

    /// <summary>
    /// Audits markdown text for footnote references and definitions, verifying sequential monotonic numerical ordering,
    /// detecting orphan references and unused definitions, and analyzing multi-paragraph footnote continuation blocks.
    /// Excludes fenced code blocks and frontmatter.
    /// </summary>
    public static FootnoteOrderAndOrphanAudit AuditFootnoteOrderAndOrphans(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new FootnoteOrderAndOrphanAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<FootnoteOrderItem>(),
                "Clean Baseline",
                "Markdown footnote order & orphan audit: 0 references · Document contains no footnotes");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var maskedLines = new string[lines.Length];
        bool inFence = false;
        string fenceMarker = string.Empty;
        bool inFrontmatter = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (i == 0 && (trimmed == "---" || trimmed == "+++"))
            {
                inFrontmatter = true;
                maskedLines[i] = string.Empty;
                continue;
            }

            if (inFrontmatter)
            {
                if (trimmed == "---" || trimmed == "+++") inFrontmatter = false;
                maskedLines[i] = string.Empty;
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = tag;
                }
                else if (tag == fenceMarker || trimmed == ":::")
                {
                    inFence = false;
                    fenceMarker = string.Empty;
                }
                maskedLines[i] = string.Empty;
                continue;
            }

            if (inFence)
            {
                maskedLines[i] = string.Empty;
                continue;
            }

            maskedLines[i] = line;
        }

        var defDict = new Dictionary<string, (int LineNumber, int ParagraphCount, string FirstLine)>(StringComparer.OrdinalIgnoreCase);
        var defRegex = new Regex(@"^[ ]{0,3}\[\^([^\]]+)\]:\s*(.*)", RegexOptions.Compiled);
        var isDefLine = new bool[maskedLines.Length];

        for (int i = 0; i < maskedLines.Length; i++)
        {
            var line = maskedLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var match = defRegex.Match(line);
            if (match.Success)
            {
                string label = match.Groups[1].Value.Trim();
                int defLine = i + 1;
                isDefLine[i] = true;
                int paragraphCount = 1;

                int j = i + 1;
                bool prevWasEmpty = false;
                while (j < maskedLines.Length)
                {
                    var nextLine = maskedLines[j];
                    if (string.IsNullOrWhiteSpace(nextLine))
                    {
                        int peek = j + 1;
                        while (peek < maskedLines.Length && string.IsNullOrWhiteSpace(maskedLines[peek])) peek++;
                        if (peek < maskedLines.Length)
                        {
                            var peekLine = maskedLines[peek];
                            if (peekLine.StartsWith("    ") || peekLine.StartsWith("\t"))
                            {
                                isDefLine[j] = true;
                                prevWasEmpty = true;
                                j++;
                                continue;
                            }
                        }
                        break;
                    }

                    if (nextLine.StartsWith("    ") || nextLine.StartsWith("\t"))
                    {
                        isDefLine[j] = true;
                        if (prevWasEmpty)
                        {
                            paragraphCount++;
                            prevWasEmpty = false;
                        }
                        j++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (!defDict.ContainsKey(label))
                {
                    defDict[label] = (defLine, paragraphCount, match.Groups[2].Value.Trim());
                }
                i = j - 1;
            }
        }

        var items = new List<FootnoteOrderItem>();
        var refRegex = new Regex(@"\[\^([^\]]+)\](?!:)", RegexOptions.Compiled);
        var inlineCodeRegex = new Regex(@"`[^`]*`", RegexOptions.Compiled);

        var referencedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalRefs = 0;
        int orphanRefs = 0;
        int outOfOrderRefs = 0;
        int maxSeenNumeric = 0;

        for (int i = 0; i < maskedLines.Length; i++)
        {
            if (isDefLine[i]) continue;
            var line = maskedLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var scrubbed = inlineCodeRegex.Replace(line, " ");
            var matches = refRegex.Matches(scrubbed);

            foreach (Match m in matches)
            {
                totalRefs++;
                string label = m.Groups[1].Value.Trim();
                referencedLabels.Add(label);

                bool isNum = int.TryParse(label, out int numVal);
                int? numNullable = isNum ? numVal : null;

                bool isOutOfOrder = false;
                if (isNum)
                {
                    if (numVal < maxSeenNumeric)
                    {
                        isOutOfOrder = true;
                        outOfOrderRefs++;
                    }
                    else
                    {
                        maxSeenNumeric = Math.Max(maxSeenNumeric, numVal);
                    }
                }

                bool hasDef = defDict.TryGetValue(label, out var defInfo);
                if (!hasDef)
                {
                    orphanRefs++;
                }

                string status;
                if (!hasDef)
                {
                    status = "⚠ Missing definition (Orphan reference)";
                }
                else if (isOutOfOrder)
                {
                    status = $"⚠ Out-of-order reference (defined line {defInfo.LineNumber})";
                }
                else if (defInfo.ParagraphCount > 1)
                {
                    status = $"✓ Defined line {defInfo.LineNumber} ({defInfo.ParagraphCount} paragraphs)";
                }
                else
                {
                    status = $"✓ Defined line {defInfo.LineNumber}";
                }

                items.Add(new FootnoteOrderItem(
                    i + 1,
                    label,
                    totalRefs,
                    isNum,
                    numNullable,
                    hasDef,
                    hasDef ? defInfo.LineNumber : 0,
                    hasDef ? defInfo.ParagraphCount : 0,
                    status));
            }
        }

        int unusedDefs = 0;
        foreach (var kvp in defDict)
        {
            if (!referencedLabels.Contains(kvp.Key))
            {
                unusedDefs++;
            }
        }

        int multiParaDefs = defDict.Values.Count(d => d.ParagraphCount > 1);

        if (totalRefs == 0 && defDict.Count == 0)
        {
            return new FootnoteOrderAndOrphanAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<FootnoteOrderItem>(),
                "Clean Baseline",
                "Markdown footnote order & orphan audit: 0 references · Document contains no footnotes");
        }

        double penalty = (orphanRefs * 15.0) + (unusedDefs * 10.0) + (outOfOrderRefs * 8.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Optimal",
            >= 75.0 => "Consistent",
            >= 60.0 => "Fair",
            >= 40.0 => "Irregular",
            _ => "Fragmented"
        };

        string summary = FormatFootnoteOrderSummary(
            totalRefs, defDict.Count, orphanRefs, unusedDefs, outOfOrderRefs, multiParaDefs, score, category);

        return new FootnoteOrderAndOrphanAudit(
            totalRefs, defDict.Count, orphanRefs, unusedDefs, outOfOrderRefs, multiParaDefs,
            score, items, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of footnote ordering and definition integrity from markdown text.
    /// </summary>
    public static string FormatFootnoteOrderSummary(string? markdownText)
        => AuditFootnoteOrderAndOrphans(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of footnote ordering metrics.
    /// </summary>
    public static string FormatFootnoteOrderSummary(
        int totalRefs, int totalDefs, int orphanRefs, int unusedDefs,
        int outOfOrderRefs, int multiParaDefs, double score, string category)
    {
        if (totalRefs == 0 && totalDefs == 0)
            return "Markdown footnote order & orphan audit: 0 references · Document contains no footnotes";

        string refStr = totalRefs == 1 ? "1 reference" : $"{totalRefs} references";
        string defStr = totalDefs == 1 ? "1 definition" : $"{totalDefs} definitions";

        var parts = new List<string>();
        if (multiParaDefs > 0) parts.Add($"{multiParaDefs} multi-paragraph");
        if (orphanRefs > 0) parts.Add($"⚠ {orphanRefs} orphan {(orphanRefs == 1 ? "ref" : "refs")}");
        if (unusedDefs > 0) parts.Add($"⚠ {unusedDefs} unused {(unusedDefs == 1 ? "def" : "defs")}");
        if (outOfOrderRefs > 0) parts.Add($"⚠ {outOfOrderRefs} out-of-order");

        string details = parts.Count > 0 ? $" · {string.Join(", ", parts)}" : "";
        string prefix = $"Markdown footnote order & orphan audit ({refStr}, {defStr} · {category} · {score:0.#}% score)";
        return details.Length > 0 ? $"{prefix}:{details}" : $"{prefix} · Sequential & balanced";
    }
    /// <summary>
    /// Audits markdown text for inline ($...$ / \(...\)) and display ($$...$$ / \[...\]) math formulas,
    /// verifying delimiter symmetry, detecting unclosed braces within TeX expressions, checking for
    /// currency collision false positives, and evaluating KaTeX formula syntax.
    /// Excludes fenced code blocks, frontmatter, and inline code spans.
    /// </summary>
    public static MarkdownMathSyntaxAudit AuditInlineMathAndTexSyntax(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new MarkdownMathSyntaxAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<InlineMathItem>(),
                "Clean Baseline",
                "Markdown math syntax audit: 0 math blocks · Document contains no math formulas");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        var maskedLines = new string[lines.Length];
        bool inFence = false;
        string fenceMarker = string.Empty;
        bool inFrontmatter = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (i == 0 && (trimmed == "---" || trimmed == "+++"))
            {
                inFrontmatter = true;
                maskedLines[i] = string.Empty;
                continue;
            }

            if (inFrontmatter)
            {
                if (trimmed == "---" || trimmed == "+++") inFrontmatter = false;
                maskedLines[i] = string.Empty;
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~") || trimmed.StartsWith(":::"))
            {
                string tag = trimmed.Length >= 3 ? trimmed.Substring(0, 3) : trimmed;
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = tag;
                }
                else if (tag == fenceMarker || trimmed == ":::")
                {
                    inFence = false;
                    fenceMarker = string.Empty;
                }
                maskedLines[i] = string.Empty;
                continue;
            }

            if (inFence)
            {
                maskedLines[i] = string.Empty;
                continue;
            }

            maskedLines[i] = line;
        }

        var items = new List<InlineMathItem>();
        int inlineCount = 0;
        int displayCount = 0;
        int malformedCount = 0;
        int unclosedBraceCount = 0;
        int currencyCollisionCount = 0;

        var inlineCodeRegex = new Regex(@"`[^`]*`", RegexOptions.Compiled);
        bool inDisplayBlock = false;
        int displayStartLine = 0;
        var displayContentBuilder = new System.Text.StringBuilder();

        for (int i = 0; i < maskedLines.Length; i++)
        {
            var rawLine = maskedLines[i];
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                if (inDisplayBlock)
                {
                    displayContentBuilder.AppendLine();
                }
                continue;
            }

            if (inDisplayBlock)
            {
                int closeIndex = rawLine.IndexOf("$$", StringComparison.Ordinal);
                if (closeIndex >= 0)
                {
                    displayContentBuilder.Append(rawLine.Substring(0, closeIndex));
                    inDisplayBlock = false;
                    string formula = displayContentBuilder.ToString().Trim();
                    int unclosed = CountUnclosedBraces(formula);
                    if (unclosed > 0) unclosedBraceCount++;

                    string snippet = formula.Length > 40 ? formula.Substring(0, 37) + "..." : formula;
                    bool isClean = unclosed == 0;
                    if (!isClean) malformedCount++;

                    items.Add(new InlineMathItem(
                        displayStartLine,
                        "Display Math ($$)",
                        snippet,
                        isClean,
                        unclosed,
                        false,
                        isClean ? $"✓ Valid display block ({formula.Length} chars)" : $"⚠ {unclosed} unclosed brace(s)"));
                    displayCount++;

                    rawLine = rawLine.Substring(closeIndex + 2);
                }
                else
                {
                    displayContentBuilder.AppendLine(rawLine);
                    continue;
                }
            }

            var line = inlineCodeRegex.Replace(rawLine, " ");

            // Check display math markers \[ ... \]
            var bracketDisplayRegex = new Regex(@"\\\[(.*?)\\\]", RegexOptions.Compiled);
            line = bracketDisplayRegex.Replace(line, m =>
            {
                displayCount++;
                string formula = m.Groups[1].Value.Trim();
                int unclosed = CountUnclosedBraces(formula);
                if (unclosed > 0) unclosedBraceCount++;
                bool isClean = unclosed == 0;
                if (!isClean) malformedCount++;
                string snippet = formula.Length > 40 ? formula.Substring(0, 37) + "..." : formula;
                items.Add(new InlineMathItem(
                    i + 1,
                    @"Display Math (\[)",
                    snippet,
                    isClean,
                    unclosed,
                    false,
                    isClean ? $"✓ Valid display math ({formula.Length} chars)" : $"⚠ {unclosed} unclosed brace(s)"));
                return " ";
            });

            // Check single line display math $$ ... $$
            var doubleDollarRegex = new Regex(@"\$\$(.*?)\$\$", RegexOptions.Compiled);
            line = doubleDollarRegex.Replace(line, m =>
            {
                displayCount++;
                string formula = m.Groups[1].Value.Trim();
                int unclosed = CountUnclosedBraces(formula);
                if (unclosed > 0) unclosedBraceCount++;
                bool isClean = unclosed == 0;
                if (!isClean) malformedCount++;
                string snippet = formula.Length > 40 ? formula.Substring(0, 37) + "..." : formula;
                items.Add(new InlineMathItem(
                    i + 1,
                    "Display Math ($$)",
                    snippet,
                    isClean,
                    unclosed,
                    false,
                    isClean ? $"✓ Valid display math ({formula.Length} chars)" : $"⚠ {unclosed} unclosed brace(s)"));
                return " ";
            });

            // Check opening multi-line display math $$
            int openDisplayIndex = line.IndexOf("$$", StringComparison.Ordinal);
            if (openDisplayIndex >= 0)
            {
                inDisplayBlock = true;
                displayStartLine = i + 1;
                displayContentBuilder.Clear();
                displayContentBuilder.Append(line.Substring(openDisplayIndex + 2));
                line = line.Substring(0, openDisplayIndex);
            }

            // Check \( ... \) inline math
            var parenInlineRegex = new Regex(@"\\\((.*?)\\\)", RegexOptions.Compiled);
            line = parenInlineRegex.Replace(line, m =>
            {
                inlineCount++;
                string formula = m.Groups[1].Value.Trim();
                int unclosed = CountUnclosedBraces(formula);
                if (unclosed > 0) unclosedBraceCount++;
                bool isClean = unclosed == 0;
                if (!isClean) malformedCount++;
                string snippet = formula.Length > 35 ? formula.Substring(0, 32) + "..." : formula;
                items.Add(new InlineMathItem(
                    i + 1,
                    @"Inline Math (\()",
                    snippet,
                    isClean,
                    unclosed,
                    false,
                    isClean ? $"✓ Valid inline math ({formula.Length} chars)" : $"⚠ {unclosed} unclosed brace(s)"));
                return " ";
            });

            // Check standard inline math $...$
            // KaTeX rule: $ not preceded by \, opener not followed by space, closer not preceded by space
            var dollarInlineRegex = new Regex(@"(?<!\\)\$(?!\s)(.*?)(?<!\s)(?<!\\)\$", RegexOptions.Compiled);
            line = dollarInlineRegex.Replace(line, m =>
            {
                string formula = m.Groups[1].Value;
                // If it looks like plain currency e.g. "$50 to $100" without math symbols or letters
                if (Regex.IsMatch(formula, @"^\d+(\.\d{2})?$"))
                {
                    // Likely currency number, ignore or count
                    return " ";
                }

                inlineCount++;
                int unclosed = CountUnclosedBraces(formula);
                if (unclosed > 0) unclosedBraceCount++;
                bool isClean = unclosed == 0;
                if (!isClean) malformedCount++;
                string snippet = formula.Length > 35 ? formula.Substring(0, 32) + "..." : formula;
                items.Add(new InlineMathItem(
                    i + 1,
                    "Inline Math ($)",
                    snippet,
                    isClean,
                    unclosed,
                    false,
                    isClean ? $"✓ Valid inline math ({formula.Length} chars)" : $"⚠ {unclosed} unclosed brace(s)"));
                return " ";
            });

            // Look for stray unescaped dollars that were not closed
            var strayDollarRegex = new Regex(@"(?<!\\)\$", RegexOptions.Compiled);
            var strayMatches = strayDollarRegex.Matches(line);
            if (strayMatches.Count > 0)
            {
                foreach (Match sm in strayMatches)
                {
                    // Check if adjacent characters are digits (currency)
                    int idx = sm.Index;
                    bool isCurrency = idx + 1 < line.Length && char.IsDigit(line[idx + 1]);
                    if (isCurrency)
                    {
                        currencyCollisionCount++;
                    }
                    else
                    {
                        malformedCount++;
                        items.Add(new InlineMathItem(
                            i + 1,
                            "Stray Delimiter ($)",
                            "$",
                            false,
                            0,
                            true,
                            "⚠ Unclosed math delimiter or unescaped dollar"));
                    }
                }
            }
        }

        if (inDisplayBlock)
        {
            malformedCount++;
            items.Add(new InlineMathItem(
                displayStartLine,
                "Display Math ($$)",
                "$$",
                false,
                0,
                true,
                "⚠ Unclosed display math block ($$)"));
        }

        int totalBlocks = inlineCount + displayCount + malformedCount;
        if (totalBlocks == 0 && currencyCollisionCount == 0)
        {
            return new MarkdownMathSyntaxAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<InlineMathItem>(),
                "Clean Baseline",
                "Markdown math syntax audit: 0 math blocks · Document contains no math formulas");
        }

        double penalty = (malformedCount * 15.0) + (unclosedBraceCount * 10.0) + (currencyCollisionCount * 4.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);
        score = Math.Round(score, 1);

        string category = score switch
        {
            >= 90.0 => "Optimal",
            >= 75.0 => "Consistent",
            >= 60.0 => "Fair",
            >= 40.0 => "Irregular",
            _ => "Fragmented"
        };

        string summary = FormatInlineMathSummary(
            totalBlocks, inlineCount, displayCount, malformedCount, unclosedBraceCount, currencyCollisionCount, score, category);

        return new MarkdownMathSyntaxAudit(
            totalBlocks, inlineCount, displayCount, malformedCount, unclosedBraceCount, currencyCollisionCount,
            score, items, category, summary);
    }

    private static int CountUnclosedBraces(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int depth = 0;
        int unclosed = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                // Escaped brace \{ or \}
                if (text[i + 1] == '{' || text[i + 1] == '}')
                {
                    i++;
                    continue;
                }
            }
            if (c == '{') depth++;
            else if (c == '}')
            {
                if (depth > 0) depth--;
                else unclosed++;
            }
        }
        return depth + unclosed;
    }

    /// <summary>
    /// Formats a human-readable summary of markdown inline and display math syntax integrity.
    /// </summary>
    public static string FormatInlineMathSummary(string? markdownText)
        => AuditInlineMathAndTexSyntax(markdownText).Summary;

    /// <summary>
    /// Formats a human-readable summary of math syntax audit metrics.
    /// </summary>
    public static string FormatInlineMathSummary(
        int totalBlocks, int inlineCount, int displayCount, int malformedCount,
        int unclosedBraceCount, int currencyCollisions, double score, string category)
    {
        if (totalBlocks == 0 && currencyCollisions == 0)
            return "Markdown math syntax audit: 0 math blocks · Document contains no math formulas";

        string blockStr = totalBlocks == 1 ? "1 math block" : $"{totalBlocks} math blocks";
        var parts = new List<string>();
        if (inlineCount > 0) parts.Add($"{inlineCount} inline");
        if (displayCount > 0) parts.Add($"{displayCount} display");
        if (malformedCount > 0) parts.Add($"⚠ {malformedCount} malformed/unclosed");
        if (unclosedBraceCount > 0) parts.Add($"⚠ {unclosedBraceCount} brace mismatch");
        if (currencyCollisions > 0) parts.Add($"{currencyCollisions} unescaped $ currency");

        string details = parts.Count > 0 ? $" · {string.Join(", ", parts)}" : "";
        string prefix = $"Markdown math syntax audit ({blockStr} · {category} · {score:0.#}% score)";
        return details.Length > 0 ? $"{prefix}:{details}" : $"{prefix} · Symmetrical & balanced";
    }

    /// <summary>
    /// Audits markdown headings for anchor slug collisions, slug ambiguity, and synchronization with internal table-of-contents links.
    /// Excludes fenced code blocks and frontmatter.
    /// </summary>
    public static HeaderAnchorAndTocAudit AuditHeaderAnchorsAndTocSync(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new HeaderAnchorAndTocAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<HeaderAnchorItem>(),
                Array.Empty<string>(),
                "Clean Baseline",
                "Markdown header anchor & TOC audit: 0 headings · Document contains no headings or TOC links");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inFence = false;
        bool inFrontmatter = false;
        var headerItems = new List<HeaderAnchorItem>();
        var slugOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var customIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allGeneratedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var customIdRegex = new Regex(@"(?:\{#([a-zA-Z0-9_\-]+)\}|<a\s+name=[""']([a-zA-Z0-9_\-]+)[""'])", RegexOptions.Compiled);
        var tocLinkRegex = new Regex(@"\[([^\]]+)\]\(#([a-zA-Z0-9_\-]+)\)", RegexOptions.Compiled);
        var tocLinksFound = new List<(string Label, string Slug, int LineNumber)>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (i == 0 && (trimmed == "---" || trimmed == "+++"))
            {
                inFrontmatter = true;
                continue;
            }
            if (inFrontmatter)
            {
                if (trimmed == "---" || trimmed == "+++") inFrontmatter = false;
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            var customMatches = customIdRegex.Matches(line);
            foreach (Match cm in customMatches)
            {
                string id = !string.IsNullOrEmpty(cm.Groups[1].Value) ? cm.Groups[1].Value : cm.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(id)) customIds.Add(id);
            }

            var tocMatches = tocLinkRegex.Matches(line);
            foreach (Match tm in tocMatches)
            {
                string label = tm.Groups[1].Value.Trim();
                string targetSlug = tm.Groups[2].Value.Trim().ToLowerInvariant();
                tocLinksFound.Add((label, targetSlug, i + 1));
            }

            var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (headingMatch.Success)
            {
                int level = headingMatch.Groups[1].Length;
                string headerRaw = headingMatch.Groups[2].Value.Trim();

                headerRaw = Regex.Replace(headerRaw, @"\s+#+\s*$", "").Trim();

                string? explicitId = null;
                var inlineCustom = customIdRegex.Match(headerRaw);
                if (inlineCustom.Success)
                {
                    explicitId = !string.IsNullOrEmpty(inlineCustom.Groups[1].Value)
                        ? inlineCustom.Groups[1].Value
                        : inlineCustom.Groups[2].Value;
                    headerRaw = customIdRegex.Replace(headerRaw, "").Trim();
                }

                string baseSlug = GenerateGitHubSlug(headerRaw);
                if (string.IsNullOrEmpty(baseSlug) && !string.IsNullOrEmpty(explicitId))
                {
                    baseSlug = explicitId.ToLowerInvariant();
                }
                else if (string.IsNullOrEmpty(baseSlug))
                {
                    baseSlug = $"section-{i + 1}";
                }

                if (!string.IsNullOrEmpty(explicitId))
                {
                    customIds.Add(explicitId);
                }

                bool hasCollision = false;
                int collisionIdx = 0;
                string finalSlug = baseSlug;

                if (slugOccurrences.TryGetValue(baseSlug, out int count))
                {
                    hasCollision = true;
                    collisionIdx = count;
                    finalSlug = $"{baseSlug}-{count}";
                    slugOccurrences[baseSlug] = count + 1;
                }
                else
                {
                    slugOccurrences[baseSlug] = 1;
                }

                allGeneratedSlugs.Add(finalSlug);
                allGeneratedSlugs.Add(baseSlug);

                string status = hasCollision
                    ? $"⚠ Duplicate heading slug collision (#{finalSlug} auto-disambiguated)"
                    : $"✓ Unique slug (#{finalSlug})";

                headerItems.Add(new HeaderAnchorItem(
                    i + 1, level, headerRaw, finalSlug, hasCollision, collisionIdx, false, status));
            }
        }

        var validAnchors = new HashSet<string>(allGeneratedSlugs, StringComparer.OrdinalIgnoreCase);
        foreach (var cid in customIds) validAnchors.Add(cid);

        var brokenTocSlugs = new List<string>();
        var referencedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in tocLinksFound)
        {
            if (validAnchors.Contains(link.Slug))
            {
                referencedSlugs.Add(link.Slug);
            }
            else
            {
                brokenTocSlugs.Add($"Line {link.LineNumber}: [{link.Label}](#{link.Slug})");
            }
        }

        var updatedHeaders = new List<HeaderAnchorItem>();
        int unreferencedCount = 0;
        foreach (var h in headerItems)
        {
            bool isRef = referencedSlugs.Contains(h.Slug);
            if (!isRef) unreferencedCount++;

            string status = h.Status;
            if (isRef) status += " · Synced in TOC";
            else if (tocLinksFound.Count > 0) status += " · Not in TOC";

            updatedHeaders.Add(h with { IsReferencedInToc = isRef, Status = status });
        }

        int totalHeaders = updatedHeaders.Count;
        int collisions = updatedHeaders.Count(h => h.HasCollision);
        int uniqueSlugs = slugOccurrences.Count;
        int brokenTocCount = brokenTocSlugs.Count;
        int tocCount = tocLinksFound.Count;

        double score = 100.0;
        if (totalHeaders > 0 || tocCount > 0)
        {
            score -= (collisions * 5.0);
            score -= (brokenTocCount * 12.0);
            if (tocCount > 0 && totalHeaders > 0 && unreferencedCount == totalHeaders)
            {
                score -= 10.0;
            }
        }
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Anchor & TOC Sync (≥90%)"
            : score >= 75.0
                ? "Moderate Anchor Collision Risk (75-89%)"
                : "Degraded Navigation & Broken TOC (<75%)";

        string summary = FormatHeaderAnchorSummary(
            totalHeaders, uniqueSlugs, collisions, tocCount, brokenTocCount,
            unreferencedCount, score, category, brokenTocSlugs);

        return new HeaderAnchorAndTocAudit(
            totalHeaders, uniqueSlugs, collisions, tocCount, brokenTocCount,
            unreferencedCount, score, updatedHeaders, brokenTocSlugs, category, summary);
    }

    private static string GenerateGitHubSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder();
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                {
                    sb.Append('-');
                }
            }
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>
    /// Formats a human-readable summary of markdown header anchor collisions and TOC synchronization.
    /// </summary>
    public static string FormatHeaderAnchorSummary(string? markdownText)
        => AuditHeaderAnchorsAndTocSync(markdownText).Summary;

    public static string FormatHeaderAnchorSummary(
        int totalHeaders, int uniqueSlugs, int collisions, int tocLinks,
        int brokenTocCount, int unreferencedHeaders, double score,
        string category, IReadOnlyList<string>? brokenSlugs = null)
    {
        if (totalHeaders == 0 && tocLinks == 0)
            return "Markdown header anchor & TOC audit: 0 headings · Document contains no headings or TOC links";

        string headerStr = totalHeaders == 1 ? "1 heading" : $"{totalHeaders} headings";
        var parts = new List<string>();
        parts.Add($"{uniqueSlugs} unique anchor {(uniqueSlugs == 1 ? "slug" : "slugs")}");
        if (collisions > 0) parts.Add($"⚠ {collisions} duplicate slug {(collisions == 1 ? "collision" : "collisions")}");
        else parts.Add("0 slug collisions");

        if (tocLinks > 0)
        {
            parts.Add($"{tocLinks} TOC {(tocLinks == 1 ? "link" : "links")}");
            if (brokenTocCount > 0) parts.Add($"⚠ {brokenTocCount} broken TOC {(brokenTocCount == 1 ? "target" : "targets")}");
            else parts.Add("100% TOC links resolved");
        }
        else
        {
            parts.Add("no TOC links defined");
        }

        string details = string.Join(" · ", parts);
        return $"Markdown header anchor & TOC audit ({headerStr} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits markdown GitHub-style callouts (> [!NOTE]), quote depth nesting,
    /// consecutive alert placement, empty alert bodies, and accessibility descriptions.
    /// </summary>
    public static MarkdownCalloutAudit AuditCalloutDepthAndSemantics(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new MarkdownCalloutAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<CalloutItem>(),
                "Clean Baseline",
                "Markdown callout alert audit: 0 alerts · Document contains no callout alert blocks");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        bool inCodeFence = false;
        var validTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NOTE", "TIP", "IMPORTANT", "WARNING", "CAUTION"
        };

        var calloutHeaderRegex = new Regex(@"^\s*(>+)\s*\[!([A-Za-z0-9_-]+)\](.*)$", RegexOptions.Compiled);
        var quotePrefixRegex = new Regex(@"^\s*>+\s*", RegexOptions.Compiled);

        var items = new List<CalloutItem>();
        int validCount = 0;
        int nestedCount = 0;
        int consecutiveCount = 0;
        int emptyBodyCount = 0;
        int unknownTypeCount = 0;
        int maxDepth = 0;

        int? lastCalloutEndLine = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            var match = calloutHeaderRegex.Match(line);
            if (match.Success)
            {
                int startLine = i + 1;
                string quoteChars = match.Groups[1].Value;
                int quoteDepth = quoteChars.Length;
                if (quoteDepth > maxDepth) maxDepth = quoteDepth;

                string alertType = match.Groups[2].Value.ToUpperInvariant();
                string inlineHeaderRemainder = match.Groups[3].Value.Trim();

                bool isValidType = validTypes.Contains(alertType);
                if (!isValidType) unknownTypeCount++;

                bool isNested = quoteDepth > 1;
                if (isNested) nestedCount++;

                bool isConsecutive = false;
                if (lastCalloutEndLine.HasValue && (startLine - lastCalloutEndLine.Value) <= 2)
                {
                    isConsecutive = true;
                    consecutiveCount++;
                }

                var bodyLines = new List<string>();
                if (!string.IsNullOrWhiteSpace(inlineHeaderRemainder))
                {
                    bodyLines.Add(inlineHeaderRemainder);
                }

                int endLine = startLine;
                int j = i + 1;
                while (j < lines.Length)
                {
                    string nextLine = lines[j];
                    string nextTrimmed = nextLine.Trim();

                    if (nextTrimmed.StartsWith("```") || nextTrimmed.StartsWith("~~~"))
                        break;

                    if (calloutHeaderRegex.IsMatch(nextLine))
                        break;

                    if (nextTrimmed.StartsWith(">"))
                    {
                        string stripped = quotePrefixRegex.Replace(nextLine, "").Trim();
                        if (!string.IsNullOrWhiteSpace(stripped))
                        {
                            bodyLines.Add(stripped);
                        }
                        endLine = j + 1;
                        j++;
                    }
                    else if (string.IsNullOrWhiteSpace(nextTrimmed))
                    {
                        if (j + 1 < lines.Length && lines[j + 1].Trim().StartsWith(">") && !calloutHeaderRegex.IsMatch(lines[j + 1]))
                        {
                            endLine = j + 1;
                            j++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                i = endLine - 1;
                lastCalloutEndLine = endLine;

                int contentLineCount = bodyLines.Count;
                int totalWords = 0;
                foreach (var bl in bodyLines)
                {
                    totalWords += bl.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                }

                bool hasEmptyBody = totalWords == 0;
                if (hasEmptyBody) emptyBodyCount++;

                if (isValidType && !isNested && !isConsecutive && !hasEmptyBody)
                {
                    validCount++;
                }

                string status;
                if (!isValidType)
                    status = $"⚠ Unknown alert type '[!{alertType}]' (expected NOTE/TIP/IMPORTANT/WARNING/CAUTION)";
                else if (isNested)
                    status = $"⚠ Invalid nested callout at depth {quoteDepth} (violates rule: do not nest alerts)";
                else if (isConsecutive)
                    status = $"⚠ Consecutive alert placement without intervening text (violates alert governance)";
                else if (hasEmptyBody)
                    status = "⚠ Missing accessible body content / empty callout block";
                else
                    status = $"✓ Standard [!{alertType}] alert ({totalWords} words)";

                items.Add(new CalloutItem(
                    startLine,
                    endLine,
                    alertType,
                    quoteDepth,
                    contentLineCount,
                    totalWords,
                    isValidType,
                    isNested,
                    isConsecutive,
                    hasEmptyBody,
                    status));
            }
        }

        int totalAlerts = items.Count;
        if (totalAlerts == 0)
        {
            return new MarkdownCalloutAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<CalloutItem>(),
                "Clean Baseline",
                "Markdown callout alert audit: 0 alerts · Document contains no callout alert blocks");
        }

        double score = 100.0;
        score -= (nestedCount * 15.0);
        score -= (consecutiveCount * 10.0);
        score -= (emptyBodyCount * 12.0);
        score -= (unknownTypeCount * 8.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Callout Semantics (≥90%)"
            : score >= 75.0
                ? "Moderate Alert Hierarchy Risk (75-89%)"
                : "Degraded Callout Governance (<75%)";

        string summary = FormatCalloutDepthSummary(
            totalAlerts, validCount, nestedCount, consecutiveCount,
            emptyBodyCount, unknownTypeCount, maxDepth, score, category);

        return new MarkdownCalloutAudit(
            totalAlerts, validCount, nestedCount, consecutiveCount,
            emptyBodyCount, unknownTypeCount, maxDepth, score, items, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown callout alert syntax and semantic nesting depth.
    /// </summary>
    public static string FormatCalloutDepthSummary(string? markdownText)
        => AuditCalloutDepthAndSemantics(markdownText).Summary;

    public static string FormatCalloutDepthSummary(
        int totalAlerts, int validCount, int nestedCount, int consecutiveCount,
        int emptyBodyCount, int unknownTypeCount, int maxDepth, double score, string category)
    {
        if (totalAlerts == 0)
            return "Markdown callout alert audit: 0 alerts · Document contains no callout alert blocks";

        string alertStr = totalAlerts == 1 ? "1 alert" : $"{totalAlerts} alerts";
        var parts = new List<string>();
        parts.Add($"{validCount} standard {(validCount == 1 ? "alert" : "alerts")}");
        if (nestedCount > 0) parts.Add($"⚠ {nestedCount} nested (depth {maxDepth})");
        else parts.Add("0 nested alerts");

        if (consecutiveCount > 0) parts.Add($"⚠ {consecutiveCount} consecutive");
        else parts.Add("0 consecutive alerts");

        if (emptyBodyCount > 0) parts.Add($"⚠ {emptyBodyCount} empty body");
        if (unknownTypeCount > 0) parts.Add($"⚠ {unknownTypeCount} unknown type");

        string details = string.Join(" · ", parts);
        return $"Markdown callout alert audit ({alertStr} · {category} · {score:0.#}% score): {details}";
    }

    /// <summary>
    /// Audits markdown image references (![alt](src "title") and &lt;img src="..." alt="..." /&gt;), evaluating alt-text accessibility,
    /// screen reader contrast, missing alt attributes, generic filename placeholders, overly verbose descriptions, and captions.
    /// </summary>
    public static MarkdownImageAccessibilityAudit AuditImageAccessibilityAndCaptions(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new MarkdownImageAccessibilityAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ImageCaptionAccessibilityItem>(),
                "Clean Baseline",
                "Markdown image accessibility audit: 0 images · Document contains no image references");
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var items = new List<ImageCaptionAccessibilityItem>();
        bool inFence = false;

        var mdImgRegex = new Regex(@"!\[(?<alt>[^\]]*)\]\((?<src>[^\s\)\""]+)(?:\s+""(?<title>[^""]*)"")?\)", RegexOptions.Compiled);
        var htmlImgRegex = new Regex(@"<img\s+(?:[^>]*?\s+)?src=[""'](?<src>[^""']+)[""'](?:[^>]*?\s+)?(?:alt=[""'](?<alt>[^""']*)[""'])?(?:[^>]*?\s+)?(?:title=[""'](?<title>[^""']*)[""'])?[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        var genericFilenameRegex = new Regex(@"^(?:image|img|screenshot|screen_shot|photo|picture|pic|graphic|figure|untitled|placeholder)(?:[-_]?\d+)?\.(?:png|jpe?g|gif|webp|svg|bmp|ico)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var placeholderTextRegex = new Regex(@"^(?:image|photo|picture|graphic|figure|placeholder|screenshot|untitled)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        int descriptiveCount = 0;
        int emptyAltCount = 0;
        int genericFilenameCount = 0;
        int overlyVerboseCount = 0;
        int captionedCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            // 1. Markdown syntax images
            var mdMatches = mdImgRegex.Matches(raw);
            foreach (Match m in mdMatches)
            {
                string alt = m.Groups["alt"].Value.Trim();
                string src = m.Groups["src"].Value.Trim();
                string? title = m.Groups["title"].Success ? m.Groups["title"].Value.Trim() : null;

                ProcessImageItem(i + 1, alt, src, title);
            }

            // 2. HTML img tags (when not already matched as markdown)
            if (!raw.Contains("![") && raw.Contains("<img", StringComparison.OrdinalIgnoreCase))
            {
                var htmlMatches = htmlImgRegex.Matches(raw);
                foreach (Match m in htmlMatches)
                {
                    string alt = m.Groups["alt"].Success ? m.Groups["alt"].Value.Trim() : string.Empty;
                    string src = m.Groups["src"].Value.Trim();
                    string? title = m.Groups["title"].Success ? m.Groups["title"].Value.Trim() : null;

                    ProcessImageItem(i + 1, alt, src, title);
                }
            }
        }

        void ProcessImageItem(int lineNum, string alt, string src, string? title)
        {
            bool hasEmptyAlt = string.IsNullOrWhiteSpace(alt);
            string srcFilename = Path.GetFileName(src);

            bool isGeneric = false;
            if (!hasEmptyAlt)
            {
                if (genericFilenameRegex.IsMatch(alt) || placeholderTextRegex.IsMatch(alt))
                {
                    isGeneric = true;
                }
                else if (string.Equals(alt, srcFilename, StringComparison.OrdinalIgnoreCase))
                {
                    isGeneric = true;
                }
            }

            bool isOverlyVerbose = alt.Length > 150;
            bool hasCaption = !string.IsNullOrWhiteSpace(title);
            bool isDescriptive = !hasEmptyAlt && !isGeneric && !isOverlyVerbose;

            if (hasEmptyAlt) emptyAltCount++;
            else if (isGeneric) genericFilenameCount++;
            else if (isOverlyVerbose) overlyVerboseCount++;
            else descriptiveCount++;

            if (hasCaption) captionedCount++;

            string status;
            if (hasEmptyAlt)
                status = "⚠ Missing alt text (Critical screen reader accessibility barrier)";
            else if (isGeneric)
                status = $"⚠ Generic placeholder / filename used as alt text ('{alt}')";
            else if (isOverlyVerbose)
                status = $"⚠ Overly verbose alt text ({alt.Length} chars > 150 max recommended)";
            else
                status = $"✓ Accessible descriptive alt text ({alt.Length} chars)" + (hasCaption ? $" · Caption: '{title}'" : "");

            items.Add(new ImageCaptionAccessibilityItem(
                lineNum,
                alt,
                src,
                title,
                hasEmptyAlt,
                isGeneric,
                isOverlyVerbose,
                hasCaption,
                isDescriptive,
                status));
        }

        int totalImages = items.Count;
        if (totalImages == 0)
        {
            return new MarkdownImageAccessibilityAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<ImageCaptionAccessibilityItem>(),
                "Clean Baseline",
                "Markdown image accessibility audit: 0 images · Document contains no image references");
        }

        double score = 100.0;
        score -= (emptyAltCount * 25.0);
        score -= (genericFilenameCount * 15.0);
        score -= (overlyVerboseCount * 8.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Image Accessibility (≥90%)"
            : score >= 75.0
                ? "Moderate Accessibility Risk (75-89%)"
                : "Degraded Accessibility Governance (<75%)";

        string summary = FormatImageCaptionAccessibilitySummary(
            totalImages, descriptiveCount, emptyAltCount, genericFilenameCount,
            overlyVerboseCount, captionedCount, score, category);

        return new MarkdownImageAccessibilityAudit(
            totalImages, descriptiveCount, emptyAltCount, genericFilenameCount,
            overlyVerboseCount, captionedCount, score, items, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown image accessibility and caption compliance.
    /// </summary>
    public static string FormatImageCaptionAccessibilitySummary(string? markdownText)
        => AuditImageAccessibilityAndCaptions(markdownText).Summary;

    public static string FormatImageCaptionAccessibilitySummary(
        int totalImages, int descriptiveCount, int emptyAltCount, int genericFilenameCount,
        int overlyVerboseCount, int captionedCount, double score, string category)
    {
        if (totalImages == 0)
            return "Markdown image accessibility audit: 0 images · Document contains no image references";

        string imgStr = totalImages == 1 ? "1 image" : $"{totalImages} images";
        var parts = new List<string>();
        parts.Add($"{descriptiveCount} descriptive alt");
        if (emptyAltCount > 0) parts.Add($"⚠ {emptyAltCount} missing alt");
        else parts.Add("0 missing alt");

        if (genericFilenameCount > 0) parts.Add($"⚠ {genericFilenameCount} generic placeholder");
        if (overlyVerboseCount > 0) parts.Add($"⚠ {overlyVerboseCount} overly verbose (>150 chars)");
        if (captionedCount > 0) parts.Add($"{captionedCount} captioned");

        string details = string.Join(" · ", parts);
        return $"Markdown image accessibility audit ({imgStr} · {category} · {score:0.#}% WCAG score): {details}";
    }

    /// <summary>
    /// Audits markdown documents for footnote and reference link definitions, detecting orphaned references,
    /// dangling definitions, duplicate targets, and cyclic alias chains.
    /// </summary>
    public static MarkdownFootnoteAndReferenceAudit AuditFootnoteAndReferenceDefinitions(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new MarkdownFootnoteAndReferenceAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<FootnoteReferenceDefinitionItem>(),
                "Clean Baseline",
                "Markdown footnote & reference link audit: 0 references · Document contains no footnote or reference definitions");
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        bool inFence = false;

        var footnoteDefRegex = new Regex(@"^[ ]{0,3}\[\^([^\]]+)\]:\s*(.*)$", RegexOptions.Compiled);
        var linkDefRegex = new Regex(@"^[ ]{0,3}\[([^\^\]]+)\]:\s*(\S+)(?:\s+(?:"".*?""|'.*?'|\(.*?\)))?$", RegexOptions.Compiled);
        var footnoteRefRegex = new Regex(@"\[\^([^\]]+)\](?!:)", RegexOptions.Compiled);
        var fullLinkRefRegex = new Regex(@"(?<!!)\[([^\]]+)\]\[([^\]]*)\]", RegexOptions.Compiled);
        var shortcutLinkRefRegex = new Regex(@"(?<!!)\[([a-zA-Z0-9_\-]+)\](?![\(:\[])", RegexOptions.Compiled);

        var footnoteDefs = new Dictionary<string, List<(int Line, string Content)>>(StringComparer.OrdinalIgnoreCase);
        var linkDefs = new Dictionary<string, List<(int Line, string Target)>>(StringComparer.OrdinalIgnoreCase);
        var footnoteRefs = new List<(int Line, string Label)>();
        var linkRefs = new List<(int Line, string Label, string Text)>();

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;

            // Check if this line is a footnote definition: [^label]: ...
            var fnDefMatch = footnoteDefRegex.Match(raw);
            if (fnDefMatch.Success)
            {
                string label = fnDefMatch.Groups[1].Value.Trim();
                string content = fnDefMatch.Groups[2].Value.Trim();
                if (!footnoteDefs.TryGetValue(label, out var list))
                {
                    list = new List<(int, string)>();
                    footnoteDefs[label] = list;
                }
                list.Add((i + 1, content));
                continue;
            }

            // Check if this line is a reference link definition: [label]: url
            var linkDefMatch = linkDefRegex.Match(raw);
            if (linkDefMatch.Success)
            {
                string label = linkDefMatch.Groups[1].Value.Trim();
                string target = linkDefMatch.Groups[2].Value.Trim();
                if (!linkDefs.TryGetValue(label, out var list))
                {
                    list = new List<(int, string)>();
                    linkDefs[label] = list;
                }
                list.Add((i + 1, target));
                continue;
            }

            // Otherwise, search for footnote references: [^label]
            var fnRefMatches = footnoteRefRegex.Matches(raw);
            foreach (Match m in fnRefMatches)
            {
                footnoteRefs.Add((i + 1, m.Groups[1].Value.Trim()));
            }

            // Search for full reference links: [text][label] or [text][]
            var fullMatches = fullLinkRefRegex.Matches(raw);
            var matchedSpans = new HashSet<int>();
            foreach (Match m in fullMatches)
            {
                string text = m.Groups[1].Value.Trim();
                string label = m.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(label)) label = text;
                linkRefs.Add((i + 1, label, text));
                matchedSpans.Add(m.Index);
            }

            // Search for shortcut reference links: [label]
            var shortMatches = shortcutLinkRefRegex.Matches(raw);
            foreach (Match m in shortMatches)
            {
                if (matchedSpans.Contains(m.Index)) continue;
                string label = m.Groups[1].Value.Trim();
                linkRefs.Add((i + 1, label, label));
            }
        }

        // Detect cyclic reference link alias chains: e.g. [a]: [b] and [b]: [a]
        var cyclicLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in linkDefs)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string curr = kvp.Key;
            visited.Add(curr);

            while (true)
            {
                if (!linkDefs.TryGetValue(curr, out var targets) || targets.Count == 0) break;
                string t = targets[0].Target;
                if (t.StartsWith("[") && t.EndsWith("]"))
                {
                    string next = t.Substring(1, t.Length - 2).Trim();
                    if (visited.Contains(next))
                    {
                        cyclicLabels.Add(curr);
                        cyclicLabels.Add(next);
                        break;
                    }
                    visited.Add(next);
                    curr = next;
                }
                else
                {
                    break;
                }
            }
        }

        var items = new List<FootnoteReferenceDefinitionItem>();
        int validPairs = 0;
        int orphanRefs = 0;
        int danglingDefs = 0;
        int dupDefs = 0;
        int cyclicCount = cyclicLabels.Count;

        var referencedFnLabels = new HashSet<string>(footnoteRefs.Select(r => r.Label), StringComparer.OrdinalIgnoreCase);
        var referencedLinkLabels = new HashSet<string>(linkRefs.Select(r => r.Label), StringComparer.OrdinalIgnoreCase);

        // Process Footnote References
        foreach (var r in footnoteRefs)
        {
            bool exists = footnoteDefs.ContainsKey(r.Label);
            if (!exists) orphanRefs++;
            else validPairs++;

            string status = exists ? $"✓ Footnote reference [^{r.Label}] bound to definition" : $"⚠ Orphaned footnote reference [^{r.Label}] without definition";
            items.Add(new FootnoteReferenceDefinitionItem(r.Line, r.Label, "FootnoteReference", null, !exists, false, false, false, status));
        }

        // Process Footnote Definitions
        foreach (var kvp in footnoteDefs)
        {
            bool referenced = referencedFnLabels.Contains(kvp.Key);
            bool isDup = kvp.Value.Count > 1;
            if (isDup) dupDefs += (kvp.Value.Count - 1);
            if (!referenced) danglingDefs++;

            foreach (var def in kvp.Value)
            {
                string status = !referenced ? $"⚠ Dangling footnote definition [^{kvp.Key}]: never referenced in text" : (isDup ? $"⚠ Duplicate footnote definition [^{kvp.Key}]:" : $"✓ Footnote definition [^{kvp.Key}]: bound to {kvp.Value.Count} references");
                items.Add(new FootnoteReferenceDefinitionItem(def.Line, kvp.Key, "FootnoteDefinition", def.Content, false, !referenced, isDup, false, status));
            }
        }

        // Process Link References
        foreach (var r in linkRefs)
        {
            bool exists = linkDefs.ContainsKey(r.Label);
            bool isCyclic = cyclicLabels.Contains(r.Label);
            if (!exists) orphanRefs++;
            else if (!isCyclic) validPairs++;

            string status = isCyclic ? $"⚠ Cyclic reference link alias chain detected for [{r.Label}]" : (exists ? $"✓ Reference link [{r.Label}] bound to definition" : $"⚠ Orphaned reference link [{r.Label}] without definition");
            items.Add(new FootnoteReferenceDefinitionItem(r.Line, r.Label, "LinkReference", r.Text, !exists, false, false, isCyclic, status));
        }

        // Process Link Definitions
        foreach (var kvp in linkDefs)
        {
            bool referenced = referencedLinkLabels.Contains(kvp.Key);
            bool isDup = kvp.Value.Count > 1;
            bool isCyclic = cyclicLabels.Contains(kvp.Key);
            if (isDup) dupDefs += (kvp.Value.Count - 1);
            if (!referenced) danglingDefs++;

            foreach (var def in kvp.Value)
            {
                string status = isCyclic ? $"⚠ Cyclic reference alias definition for [{kvp.Key}] -> {def.Target}" : (!referenced ? $"⚠ Dangling link definition [{kvp.Key}]: never referenced in text" : (isDup ? $"⚠ Duplicate link definition [{kvp.Key}]:" : $"✓ Link definition [{kvp.Key}]: bound to '{def.Target}'"));
                items.Add(new FootnoteReferenceDefinitionItem(def.Line, kvp.Key, "LinkDefinition", def.Target, false, !referenced, isDup, isCyclic, status));
            }
        }

        int totalRefs = footnoteRefs.Count + linkRefs.Count;
        int totalDefs = footnoteDefs.Values.Sum(v => v.Count) + linkDefs.Values.Sum(v => v.Count);

        if (totalRefs == 0 && totalDefs == 0)
        {
            return new MarkdownFootnoteAndReferenceAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<FootnoteReferenceDefinitionItem>(),
                "Clean Baseline",
                "Markdown footnote & reference link audit: 0 references · Document contains no footnote or reference definitions");
        }

        double score = 100.0;
        score -= (orphanRefs * 20.0);
        score -= (danglingDefs * 10.0);
        score -= (dupDefs * 10.0);
        score -= (cyclicCount * 30.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Footnote & Reference Governance (≥90%)"
            : score >= 75.0
                ? "Moderate Reference Integrity Risk (75-89%)"
                : "Degraded Reference Integrity (<75%)";

        string summary = FormatFootnoteAndReferenceDefinitionsSummary(
            totalRefs, totalDefs, validPairs, orphanRefs, danglingDefs, dupDefs, cyclicCount, score, category);

        return new MarkdownFootnoteAndReferenceAudit(
            totalRefs, totalDefs, validPairs, orphanRefs, danglingDefs, dupDefs, cyclicCount, score, items, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of footnote and reference definition compliance.
    /// </summary>
    public static string FormatFootnoteAndReferenceDefinitionsSummary(string? markdownText)
        => AuditFootnoteAndReferenceDefinitions(markdownText).Summary;

    public static string FormatFootnoteAndReferenceDefinitionsSummary(
        int totalRefs, int totalDefs, int validPairs, int orphans, int dangling,
        int duplicates, int cycles, double score, string category)
    {
        if (totalRefs == 0 && totalDefs == 0)
            return "Markdown footnote & reference link audit: 0 references · Document contains no footnote or reference definitions";

        var parts = new List<string>();
        parts.Add($"{validPairs} valid {(validPairs == 1 ? "pair" : "pairs")}");

        if (orphans > 0) parts.Add($"⚠ {orphans} orphaned {(orphans == 1 ? "reference" : "references")}");
        else parts.Add("0 orphans");

        if (dangling > 0) parts.Add($"⚠ {dangling} dangling {(dangling == 1 ? "definition" : "definitions")}");
        else parts.Add("0 dangling");

        if (duplicates > 0) parts.Add($"⚠ {duplicates} duplicate {(duplicates == 1 ? "definition" : "definitions")}");
        if (cycles > 0) parts.Add($"⚠ {cycles} cyclic {(cycles == 1 ? "loop" : "loops")}");

        string details = string.Join(" · ", parts);
        return $"Markdown footnote & reference link audit ({totalRefs} refs, {totalDefs} defs · {category} · {score:0.#}% integrity score): {details}";
    }

    /// <summary>
    /// Audits markdown documents for table structure, pipe column counts, header delimiter rows (|:---|:---:|---:|),
    /// ragged row parity, unescaped pipes within cells, and outer border pipe consistency.
    /// </summary>
    public static MarkdownTableAlignmentAudit AuditTableAlignmentAndDelimiters(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new MarkdownTableAlignmentAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<TableStructureItem>(),
                "Clean Baseline",
                "Markdown table structure & alignment audit: 0 tables · Document contains no tables");
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        bool inFence = false;

        var tables = new List<TableStructureItem>();
        int tableIndexCounter = 1;
        int totalRowsAcc = 0;
        int totalColsAcc = 0;
        int totalRaggedAcc = 0;
        int totalDelimiterIssuesAcc = 0;
        int totalUnescapedPipesAcc = 0;
        int totalOuterPipesMissingAcc = 0;

        var delimiterPattern = new Regex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$", RegexOptions.Compiled);

        int i = 0;
        while (i < lines.Length)
        {
            string raw = lines[i];
            string trimmed = raw.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inFence = !inFence;
                i++;
                continue;
            }
            if (inFence)
            {
                i++;
                continue;
            }

            // A table requires a header line and a delimiter line immediately following
            if (trimmed.Contains('|') && i + 1 < lines.Length)
            {
                string nextRaw = lines[i + 1];
                string nextTrimmed = nextRaw.Trim();

                if (delimiterPattern.IsMatch(nextTrimmed) || (nextTrimmed.Contains('|') && nextTrimmed.Contains("-")))
                {
                    // Found a table starting at line i + 1 (1-based)
                    int startLine = i + 1;
                    string headerRaw = raw;
                    string delimRaw = nextRaw;

                    List<string> ParseRowCells(string rowText)
                    {
                        string t = rowText.Trim();
                        if (t.StartsWith("|")) t = t.Substring(1);
                        if (t.EndsWith("|") && !t.EndsWith(@"\|")) t = t.Substring(0, t.Length - 1);
                        var cellList = new List<string>();
                        var parts = Regex.Split(t, @"(?<!\\)\|");
                        foreach (var part in parts)
                        {
                            cellList.Add(part.Trim());
                        }
                        return cellList;
                    }

                    var headerCells = ParseRowCells(headerRaw);
                    int headerColCount = headerCells.Count;

                    var delimCells = ParseRowCells(delimRaw);
                    bool validDelimiter = delimCells.Count == headerColCount;
                    var alignments = new List<string>();

                    foreach (var dc in delimCells)
                    {
                        string d = dc.Trim();
                        bool startColon = d.StartsWith(":");
                        bool endColon = d.EndsWith(":");
                        string dashPart = d.Trim(':');
                        if (dashPart.Length >= 2 && dashPart.All(c => c == '-'))
                        {
                            if (startColon && endColon) alignments.Add("center");
                            else if (endColon) alignments.Add("right");
                            else if (startColon) alignments.Add("left");
                            else alignments.Add("default");
                        }
                        else
                        {
                            alignments.Add("invalid");
                            validDelimiter = false;
                        }
                    }

                    int tableRows = 2; // header + delimiter
                    int raggedCount = 0;
                    int unescapedPipes = 0;
                    int missingOuterPipes = 0;

                    if (!headerRaw.Trim().StartsWith("|") || !headerRaw.Trim().EndsWith("|")) missingOuterPipes++;
                    if (!delimRaw.Trim().StartsWith("|") || !delimRaw.Trim().EndsWith("|")) missingOuterPipes++;
                    if (!validDelimiter) totalDelimiterIssuesAcc++;

                    int bodyIdx = i + 2;
                    while (bodyIdx < lines.Length)
                    {
                        string bodyRaw = lines[bodyIdx];
                        string bodyTrim = bodyRaw.Trim();

                        if (string.IsNullOrWhiteSpace(bodyTrim) || !bodyTrim.Contains('|'))
                            break;

                        tableRows++;
                        if (!bodyTrim.StartsWith("|") || !bodyTrim.EndsWith("|")) missingOuterPipes++;

                        var bodyCells = ParseRowCells(bodyRaw);
                        if (bodyCells.Count != headerColCount)
                        {
                            raggedCount++;
                        }

                        // Check for unescaped pipe occurrences inside cell content
                        foreach (var cell in bodyCells)
                        {
                            if (cell.Contains('|'))
                            {
                                unescapedPipes++;
                            }
                        }

                        bodyIdx++;
                    }

                    int endLine = bodyIdx;
                    int cellCount = tableRows * headerColCount;

                    totalRowsAcc += tableRows;
                    totalColsAcc += headerColCount;
                    totalRaggedAcc += raggedCount;
                    totalUnescapedPipesAcc += unescapedPipes;
                    totalOuterPipesMissingAcc += missingOuterPipes;

                    bool isOuterClean = missingOuterPipes == 0;
                    string status = (raggedCount > 0 || !validDelimiter)
                        ? $"⚠ Table #{tableIndexCounter} (L{startLine}-L{endLine}): {headerColCount} cols, {raggedCount} ragged rows, {(validDelimiter ? "valid delimiter" : "invalid delimiter")}"
                        : $"✓ Table #{tableIndexCounter} (L{startLine}-L{endLine}): {headerColCount} cols, {tableRows} rows, balanced alignments ({string.Join("/", alignments)})";

                    tables.Add(new TableStructureItem(
                        tableIndexCounter++,
                        startLine,
                        endLine,
                        headerColCount,
                        tableRows,
                        raggedCount,
                        cellCount,
                        unescapedPipes,
                        isOuterClean,
                        validDelimiter,
                        alignments,
                        status));

                    i = bodyIdx;
                    continue;
                }
            }

            i++;
        }

        if (tables.Count == 0)
        {
            return new MarkdownTableAlignmentAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<TableStructureItem>(),
                "Clean Baseline",
                "Markdown table structure & alignment audit: 0 tables · Document contains no tables");
        }

        double score = 100.0;
        score -= (totalDelimiterIssuesAcc * 25.0);
        score -= (totalRaggedAcc * 15.0);
        score -= (totalUnescapedPipesAcc * 10.0);
        score -= (totalOuterPipesMissingAcc * 5.0);
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Table Structure & Alignment (≥90%)"
            : score >= 75.0
                ? "Moderate Table Delimiter / Parity Risk (75-89%)"
                : "Degraded Table Syntax & Ragged Rows (<75%)";

        string summary = FormatTableAlignmentSummary(
            tables.Count, totalRowsAcc, totalColsAcc, totalRaggedAcc,
            totalDelimiterIssuesAcc, totalUnescapedPipesAcc, totalOuterPipesMissingAcc, score, category);

        return new MarkdownTableAlignmentAudit(
            tables.Count,
            totalRowsAcc,
            totalColsAcc,
            totalRaggedAcc,
            totalDelimiterIssuesAcc,
            totalUnescapedPipesAcc,
            totalOuterPipesMissingAcc,
            score,
            tables,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of table alignment and delimiter integrity.
    /// </summary>
    public static string FormatTableAlignmentAndDelimitersSummary(string? markdownText)
        => AuditTableAlignmentAndDelimiters(markdownText).Summary;

    public static string FormatTableAlignmentSummary(
        int totalTables, int totalRows, int totalCols, int raggedRows,
        int delimiterIssues, int unescapedPipes, int outerPipesMissing, double score, string category)
    {
        if (totalTables == 0)
            return "Markdown table structure & alignment audit: 0 tables · Document contains no tables";

        string tblLabel = totalTables == 1 ? "1 table" : $"{totalTables} tables";
        var parts = new List<string>();
        parts.Add($"{totalRows} rows, {totalCols} total cols");

        if (raggedRows > 0) parts.Add($"⚠ {raggedRows} ragged {(raggedRows == 1 ? "row" : "rows")}");
        else parts.Add("0 ragged rows");

        if (delimiterIssues > 0) parts.Add($"⚠ {delimiterIssues} delimiter {(delimiterIssues == 1 ? "issue" : "issues")}");
        if (unescapedPipes > 0) parts.Add($"⚠ {unescapedPipes} unescaped {(unescapedPipes == 1 ? "pipe" : "pipes")}");
        if (outerPipesMissing > 0) parts.Add($"⚠ {outerPipesMissing} missing outer pipes");

        string details = string.Join(" · ", parts);
        return $"Markdown table structure & alignment audit ({tblLabel} · {category} · {score:0.#}% compliance score): {details}";
    }

    /// <summary>
    /// Audits markdown task lists, checkboxes (- [ ], - [x]), priority tags ([p0]-[p3], [high], #urgent),
    /// due date annotations (@due(YYYY-MM-DD), due:YYYY-MM-DD), overdue items, and task tracking rigor.
    /// </summary>
    public static MarkdownTaskPriorityAudit AuditTaskListPriorityAndDueDates(
        string? markdownText, DateTime? referenceDate = null)
    {
        var refDate = referenceDate ?? DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return new MarkdownTaskPriorityAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<TaskPriorityItem>(),
                "Clean Baseline",
                "Markdown task list priority & due date audit: 0 tasks · Document contains no checklist items");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        bool inCodeFence = false;
        bool inHtmlComment = false;
        string currentSection = "General";

        var taskItems = new List<TaskPriorityItem>();
        var taskRegex = new Regex(@"^\s*(?:[-*+]|\d+\.)\s+\[([ xX])\]\s*(.*)$", RegexOptions.Compiled);
        var dueDateRegex = new Regex(@"(?:@due\(|due:|📅\s*)([0-9]{4}-[0-9]{2}-[0-9]{2})\)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var p0Regex = new Regex(@"(?:\[p0\]|\[high\]|#urgent|!urgent|!high)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var p1Regex = new Regex(@"(?:\[p1\]|\[med\]|\[medium\]|#medium)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var p2Regex = new Regex(@"(?:\[p2\]|\[p3\]|\[low\]|#low)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            if (trimmed.StartsWith("<!--") && !trimmed.EndsWith("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;

            if (trimmed.StartsWith("#"))
            {
                int hIndex = 0;
                while (hIndex < trimmed.Length && trimmed[hIndex] == '#') hIndex++;
                currentSection = trimmed[hIndex..].Trim();
                if (string.IsNullOrWhiteSpace(currentSection)) currentSection = "General";
                continue;
            }

            var match = taskRegex.Match(line);
            if (match.Success)
            {
                char check = match.Groups[1].Value[0];
                bool isCompleted = check == 'x' || check == 'X';
                string text = match.Groups[2].Value.Trim();

                string priority = "None";
                if (p0Regex.IsMatch(text)) priority = "High";
                else if (p1Regex.IsMatch(text)) priority = "Medium";
                else if (p2Regex.IsMatch(text)) priority = "Low";

                DateTime? dueDate = null;
                bool isOverdue = false;
                var dueMatch = dueDateRegex.Match(text);
                if (dueMatch.Success && DateTime.TryParse(dueMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                {
                    dueDate = dt;
                    if (!isCompleted && dt.Date < refDate.Date)
                    {
                        isOverdue = true;
                    }
                }

                string status;
                if (isCompleted) status = "✓ Completed";
                else if (isOverdue) status = $"⚠ Overdue (due {dueDate:yyyy-MM-dd})";
                else if (dueDate != null) status = $"Due {dueDate:yyyy-MM-dd}";
                else if (priority != "None") status = $"{priority} priority";
                else status = "Pending";

                taskItems.Add(new TaskPriorityItem(i + 1, currentSection, text, isCompleted, priority, dueDate, isOverdue, status));
            }
        }

        if (taskItems.Count == 0)
        {
            return new MarkdownTaskPriorityAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<TaskPriorityItem>(),
                "Clean Baseline",
                "Markdown task list priority & due date audit: 0 tasks · Document contains no checklist items");
        }

        int totalTasks = taskItems.Count;
        int completedTasks = taskItems.Count(t => t.IsCompleted);
        int pendingTasks = totalTasks - completedTasks;
        int highCount = taskItems.Count(t => t.Priority == "High");
        int medCount = taskItems.Count(t => t.Priority == "Medium");
        int lowCount = taskItems.Count(t => t.Priority == "Low");
        int overdueCount = taskItems.Count(t => t.IsOverdue);
        int taggedCount = taskItems.Count(t => t.Priority != "None" || t.DueDate != null);
        double completionRate = Math.Round((double)completedTasks / totalTasks * 100.0, 1);

        double score = 100.0;
        if (overdueCount > 0)
        {
            score -= Math.Min(45.0, overdueCount * 15.0);
        }
        if (totalTasks > 5 && taggedCount == 0)
        {
            score -= 20.0;
        }
        if (pendingTasks > 0 && completionRate < 25.0 && totalTasks >= 4)
        {
            score -= 10.0;
        }
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category = score >= 90.0
            ? "Optimal Task Tracking & Deadline Rigor (≥90%)"
            : score >= 75.0
                ? "Good Task Progress & Priority Structure (75-89%)"
                : score >= 50.0
                    ? "Moderate Task List Drift & Overdue Items (50-74%)"
                    : "Severe Task Priority & Deadline Disorganization (<50%)";

        string summary = FormatTaskListPrioritySummary(
            totalTasks, completedTasks, pendingTasks, highCount, overdueCount, completionRate, score, category);

        return new MarkdownTaskPriorityAudit(
            totalTasks,
            completedTasks,
            pendingTasks,
            highCount,
            medCount,
            lowCount,
            overdueCount,
            taggedCount,
            completionRate,
            score,
            taskItems,
            category,
            summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown task list priorities, due dates, and progress.
    /// </summary>
    public static string FormatTaskListPrioritySummary(string? markdownText, DateTime? referenceDate = null)
        => AuditTaskListPriorityAndDueDates(markdownText, referenceDate).Summary;

    public static string FormatTaskListPrioritySummary(
        int totalTasks, int completedTasks, int pendingTasks, int highCount,
        int overdueCount, double completionRate, double score, string category)
    {
        if (totalTasks == 0)
            return "Markdown task list priority & due date audit: 0 tasks · Document contains no checklist items";

        string taskStr = totalTasks == 1 ? "1 task" : $"{totalTasks} tasks";
        var parts = new List<string>();
        parts.Add($"{completedTasks}/{totalTasks} completed ({completionRate:0.#}%)");

        if (overdueCount > 0) parts.Add($"⚠ {overdueCount} overdue");
        else parts.Add("0 overdue");

        if (highCount > 0) parts.Add($"{highCount} high-priority");
        if (pendingTasks > 0) parts.Add($"{pendingTasks} pending");

        string details = string.Join(" · ", parts);
        return $"Markdown task list priority & due date audit ({taskStr} · {category} · {score:0.#}% rigor score): {details}";
    }

    /// <summary>
    /// Audits markdown blockquote callout levels, alert syntax (> [!NOTE]), deep nesting levels (> 3),
    /// empty quote lines, and citation attribution footers (-- Author).
    /// </summary>
    public static MarkdownBlockquoteCalloutAudit AuditBlockquoteCalloutsAndAttribution(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown blockquote callout & attribution audit: 0 quote lines · Document contains no blockquotes";
            return new MarkdownBlockquoteCalloutAudit(
                0, 0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<BlockquoteCalloutLevelItem>(),
                "Clean Baseline",
                emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var items = new List<BlockquoteCalloutLevelItem>();
        bool inCodeFence = false;
        bool inHtmlComment = false;

        int totalQuoteLines = 0;
        int totalQuoteBlocks = 0;
        int maxNestingDepth = 0;
        int standardAlertCount = 0;
        int customAlertCount = 0;
        int excessiveNestingCount = 0;
        int emptyQuoteCount = 0;
        int attributionCount = 0;
        bool inBlockquote = false;

        var standardAlerts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NOTE", "TIP", "IMPORTANT", "WARNING", "CAUTION"
        };

        for (int i = 0; i < lines.Length; i++)
        {
            string rawLine = lines[i];
            string trimmed = rawLine.Trim();

            // Code fence tracking
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                inBlockquote = false;
                continue;
            }
            if (inCodeFence) continue;

            // HTML comment tracking
            if (trimmed.StartsWith("<!--") && !trimmed.Contains("-->"))
            {
                inHtmlComment = true;
                inBlockquote = false;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }

            if (trimmed.StartsWith(">"))
            {
                if (!inBlockquote)
                {
                    totalQuoteBlocks++;
                    inBlockquote = true;
                }

                totalQuoteLines++;

                // Calculate nesting depth (count of leading > with optional spaces)
                int depth = 0;
                int idx = 0;
                while (idx < trimmed.Length && (trimmed[idx] == '>' || char.IsWhiteSpace(trimmed[idx])))
                {
                    if (trimmed[idx] == '>') depth++;
                    idx++;
                }

                if (depth > maxNestingDepth) maxNestingDepth = depth;
                bool isExcessive = depth > 3;
                if (isExcessive) excessiveNestingCount++;

                string content = idx < trimmed.Length ? trimmed.Substring(idx).Trim() : string.Empty;
                bool isEmpty = string.IsNullOrWhiteSpace(content);
                if (isEmpty) emptyQuoteCount++;

                bool isAlert = false;
                string alertType = string.Empty;
                bool isCustom = false;
                bool isAttribution = false;
                string? attributionText = null;

                if (!isEmpty)
                {
                    // Check alert callout pattern [!NOTE]
                    var matchAlert = System.Text.RegularExpressions.Regex.Match(content, @"^\[!([A-Za-z0-9_-]+)\](?:\+|-)?(?:\s+(.*))?$");
                    if (matchAlert.Success)
                    {
                        isAlert = true;
                        string tag = matchAlert.Groups[1].Value.ToUpperInvariant();
                        alertType = tag;
                        if (standardAlerts.Contains(tag))
                        {
                            standardAlertCount++;
                        }
                        else
                        {
                            isCustom = true;
                            customAlertCount++;
                        }
                    }
                    else
                    {
                        // Check attribution footer: -- Author or — Author or ― Author
                        var matchAttr = System.Text.RegularExpressions.Regex.Match(content, @"^(?:--|—|―)\s*(.+)$");
                        if (matchAttr.Success)
                        {
                            isAttribution = true;
                            attributionText = matchAttr.Groups[1].Value.Trim();
                            attributionCount++;
                        }
                    }
                }

                string status = isExcessive ? "Excessive Nesting (>3)" :
                                isEmpty ? "Empty Quote Line" :
                                isAlert ? (isCustom ? $"Custom Alert [{alertType}]" : $"Alert [{alertType}]") :
                                isAttribution ? $"Attribution: {attributionText}" :
                                "Standard Quote Content";

                items.Add(new BlockquoteCalloutLevelItem(
                    i + 1, depth, content, isAlert, alertType, isCustom, isExcessive, isEmpty, isAttribution, attributionText, status));
            }
            else
            {
                if (inBlockquote && string.IsNullOrWhiteSpace(trimmed))
                {
                    inBlockquote = false;
                }
            }
        }

        double score = 100.0;
        score -= excessiveNestingCount * 12.0;
        score -= emptyQuoteCount * 8.0;
        score -= customAlertCount * 6.0;
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category;
        if (totalQuoteLines == 0) category = "Clean Baseline";
        else if (score >= 90.0) category = "Optimal Blockquote & Callout Governance (≥90%)";
        else if (score >= 75.0) category = "Moderate Blockquote Hierarchy (75–89%)";
        else category = "Excessive Nesting & Callout Irregularities (<75%)";

        string summary = FormatBlockquoteCalloutSummary(
            totalQuoteLines, totalQuoteBlocks, maxNestingDepth, standardAlertCount,
            customAlertCount, excessiveNestingCount, emptyQuoteCount, attributionCount, score, category);

        return new MarkdownBlockquoteCalloutAudit(
            totalQuoteLines, totalQuoteBlocks, maxNestingDepth, standardAlertCount,
            customAlertCount, excessiveNestingCount, emptyQuoteCount, attributionCount,
            score, items, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown blockquote nesting levels and callouts.
    /// </summary>
    public static string FormatBlockquoteCalloutAndAttributionSummary(string? markdownText)
        => AuditBlockquoteCalloutsAndAttribution(markdownText).Summary;

    public static string FormatBlockquoteCalloutSummary(
        int totalQuoteLines, int totalQuoteBlocks, int maxNestingDepth, int standardAlertCount,
        int customAlertCount, int excessiveNestingCount, int emptyQuoteCount, int attributionCount,
        double score, string category)
    {
        if (totalQuoteLines == 0)
            return "Markdown blockquote callout & attribution audit: 0 quote lines · Document contains no blockquotes";

        string lineStr = totalQuoteLines == 1 ? "1 quote line" : $"{totalQuoteLines} quote lines";
        string blockStr = totalQuoteBlocks == 1 ? "1 block" : $"{totalQuoteBlocks} blocks";
        var parts = new List<string>();
        parts.Add($"{lineStr} across {blockStr} (Max depth {maxNestingDepth})");

        if (standardAlertCount > 0 || customAlertCount > 0)
        {
            string alertStr = customAlertCount > 0
                ? $"{standardAlertCount} standard + {customAlertCount} custom alerts"
                : $"{standardAlertCount} alert callouts";
            parts.Add(alertStr);
        }

        if (attributionCount > 0)
            parts.Add($"{attributionCount} citation attributions");

        if (excessiveNestingCount > 0)
            parts.Add($"⚠ {excessiveNestingCount} deep nested (>3)");

        if (emptyQuoteCount > 0)
            parts.Add($"⚠ {emptyQuoteCount} empty quote lines");

        string details = string.Join(" · ", parts);
        return $"Markdown blockquote callout & attribution audit ({details} · {category} · {score:0.#}% rigor score)";
    }

    /// <summary>
    /// Audits markdown text for raw and numeric HTML entities, malformed/broken entities missing semicolons,
    /// ambiguous character escapes, and unescaped angle brackets outside code blocks.
    /// </summary>
    public static MarkdownHtmlEntitiesAudit AuditHtmlEntitiesAndEscapedCharacters(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown HTML entities & character escape audit: Clean text · 0 entities or escapes detected";
            return new MarkdownHtmlEntitiesAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<HtmlEntityEscapeItem>(),
                "Clean Baseline",
                emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var items = new List<HtmlEntityEscapeItem>();

        int totalEntities = 0;
        int namedCount = 0;
        int numericCount = 0;
        int brokenCount = 0;
        int ambiguousEscapeCount = 0;
        int unescapedBracketCount = 0;

        bool inCodeFence = false;
        bool inHtmlComment = false;

        var recognizedNamedEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nbsp", "amp", "lt", "gt", "quot", "apos", "copy", "reg", "trade", "mdash", "ndash",
            "hellip", "euro", "pound", "yen", "cent", "bull", "sect", "para", "middot", "deg",
            "plusmn", "frac12", "frac14", "times", "divide", "laquo", "raquo", "shy", "micro",
            "acute", "macr", "not", "brvbar", "curren", "iexcl", "iquest"
        };

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Track multi-line code fences
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            // Track HTML comments
            if (trimmed.StartsWith("<!--") && !trimmed.Contains("-->"))
            {
                inHtmlComment = true;
                continue;
            }
            if (inHtmlComment)
            {
                if (trimmed.Contains("-->")) inHtmlComment = false;
                continue;
            }

            // Remove inline code spans (`...`) so code like `List<string>` isn't flagged
            string scrubbedLine = System.Text.RegularExpressions.Regex.Replace(line, @"`[^`]+`", m => new string(' ', m.Length));

            // Check valid named entities: &name;
            var namedMatches = System.Text.RegularExpressions.Regex.Matches(scrubbedLine, @"&([a-zA-Z]{2,12});");
            foreach (System.Text.RegularExpressions.Match m in namedMatches)
            {
                totalEntities++;
                namedCount++;
                string entName = m.Groups[1].Value;
                bool isRecognized = recognizedNamedEntities.Contains(entName);
                string status = isRecognized ? $"Recognized Entity [&{entName};]" : $"Custom Named Entity [&{entName};]";
                items.Add(new HtmlEntityEscapeItem(
                    i + 1, m.Index + 1, m.Value, "NamedEntity", entName, true, false, false, "Standard named entity", status));
            }

            // Check valid numeric entities: &#123; or &#x7B;
            var numMatches = System.Text.RegularExpressions.Regex.Matches(scrubbedLine, @"&#(?:([0-9]{1,7})|x([0-9a-fA-F]{1,6}));");
            foreach (System.Text.RegularExpressions.Match m in numMatches)
            {
                totalEntities++;
                numericCount++;
                string numVal = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                string kind = m.Groups[1].Success ? "DecimalEntity" : "HexEntity";
                items.Add(new HtmlEntityEscapeItem(
                    i + 1, m.Index + 1, m.Value, kind, numVal, true, false, false, "Standard numeric entity", $"Numeric Entity [{m.Value}]"));
            }

            // Check broken entities missing semicolon: &amp or &nbsp followed by space, punctuation or end of line (not preceded by alphanumeric)
            var brokenMatches = System.Text.RegularExpressions.Regex.Matches(scrubbedLine, @"&([a-zA-Z]{2,10})(?=[ \t\r\n,\.!?]|$)(?!;)");
            foreach (System.Text.RegularExpressions.Match m in brokenMatches)
            {
                string ent = m.Groups[1].Value;
                if (recognizedNamedEntities.Contains(ent))
                {
                    brokenCount++;
                    items.Add(new HtmlEntityEscapeItem(
                        i + 1, m.Index + 1, m.Value, "BrokenEntity", ent, false, true, false, $"Append missing semicolon: &{ent};", $"Broken Entity [&{ent}] missing semicolon"));
                }
            }

            // Check ambiguous character escapes: \(alphanumeric)
            var escapeMatches = System.Text.RegularExpressions.Regex.Matches(scrubbedLine, @"\\([a-zA-Z0-9])");
            foreach (System.Text.RegularExpressions.Match m in escapeMatches)
            {
                ambiguousEscapeCount++;
                string escChar = m.Groups[1].Value;
                items.Add(new HtmlEntityEscapeItem(
                    i + 1, m.Index + 1, m.Value, "AmbiguousEscape", escChar, false, false, true, $"Remove backslash or use standard punctuation escape: '{escChar}'", $"Ambiguous escape sequence [\\{escChar}]"));
            }

            // Check unescaped raw angle brackets outside HTML tags/autolinks
            string lineWithoutTags = System.Text.RegularExpressions.Regex.Replace(scrubbedLine, @"</?[a-zA-Z][a-zA-Z0-9_-]*(?:\s+[^>]*)?/?>", m => new string(' ', m.Length));
            lineWithoutTags = System.Text.RegularExpressions.Regex.Replace(lineWithoutTags, @"<https?://[^>]+>", m => new string(' ', m.Length));
            
            if (lineWithoutTags.TrimStart().StartsWith(">"))
            {
                int firstGt = lineWithoutTags.IndexOf('>');
                if (firstGt >= 0)
                {
                    lineWithoutTags = lineWithoutTags.Substring(0, firstGt) + " " + lineWithoutTags.Substring(firstGt + 1);
                }
            }

            var bracketMatches = System.Text.RegularExpressions.Regex.Matches(lineWithoutTags, @"[<>]");
            foreach (System.Text.RegularExpressions.Match m in bracketMatches)
            {
                unescapedBracketCount++;
                string bracket = m.Value;
                string rec = bracket == "<" ? "Replace with &lt; or wrap in code span" : "Replace with &gt; or wrap in code span";
                items.Add(new HtmlEntityEscapeItem(
                    i + 1, m.Index + 1, bracket, "UnescapedBracket", bracket, false, false, true, rec, $"Unescaped raw bracket [{bracket}]"));
            }
        }

        double score = 100.0;
        score -= brokenCount * 12.0;
        score -= unescapedBracketCount * 6.0;
        score -= ambiguousEscapeCount * 4.0;
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category;
        if (totalEntities == 0 && brokenCount == 0 && ambiguousEscapeCount == 0 && unescapedBracketCount == 0) category = "Clean Baseline";
        else if (score >= 90.0) category = "Optimal Entity & Character Encoding Governance (≥90%)";
        else if (score >= 75.0) category = "Moderate Encoding Regularity with Minor Drift (75–89%)";
        else category = "Severe Broken Entities & Escape Ambiguities (<75%)";

        string summary = FormatHtmlEntitiesSummary(
            totalEntities, namedCount, numericCount, brokenCount, ambiguousEscapeCount, unescapedBracketCount, score, category);

        return new MarkdownHtmlEntitiesAudit(
            totalEntities, namedCount, numericCount, brokenCount, ambiguousEscapeCount, unescapedBracketCount,
            score, items, category, summary);
    }

    /// <summary>
    /// Formats a human-readable summary of markdown HTML entities and character escape audits.
    /// </summary>
    public static MarkdownHtmlEntitiesAudit AuditMarkdownHtmlEntities(string? markdownText)
        => AuditHtmlEntitiesAndEscapedCharacters(markdownText);

    public static string FormatHtmlEntitiesSummary(string? markdownText)
        => AuditHtmlEntitiesAndEscapedCharacters(markdownText).Summary;

    public static string FormatHtmlEntitiesSummary(
        int totalEntities, int namedCount, int numericCount, int brokenCount,
        int ambiguousEscapeCount, int unescapedBracketCount, double score, string category)
    {
        if (totalEntities == 0 && ambiguousEscapeCount == 0 && unescapedBracketCount == 0 && brokenCount == 0)
            return "Markdown HTML entities & character escape audit: Clean text · 0 entities or escapes detected";

        var parts = new List<string>();
        if (totalEntities > 0)
        {
            parts.Add($"{totalEntities} entities ({namedCount} named, {numericCount} numeric)");
        }
        else
        {
            parts.Add("0 entities");
        }

        if (brokenCount > 0)
            parts.Add($"⚠ {brokenCount} broken entities missing ';'");

        if (ambiguousEscapeCount > 0)
            parts.Add($"⚠ {ambiguousEscapeCount} ambiguous escapes");

        if (unescapedBracketCount > 0)
            parts.Add($"⚠ {unescapedBracketCount} unescaped brackets");

        string details = string.Join(" · ", parts);
        return $"Markdown HTML entities & character escape audit ({details} · {category} · {score:0.#}% hygiene score)";
    }

    /// <summary>
    /// Audits markdown text for GFM footnote references ([^1], [^note]), definitions ([^1]: text),
    /// orphaned references, unreferenced definitions, and sequential numeric ordering.
    /// </summary>
    public static MarkdownFootnoteAudit AuditFootnoteReferencesAndDefinitions(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown footnote audit: Clean document · 0 footnote references or definitions detected";
            return new MarkdownFootnoteAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownFootnoteSequenceItem>(),
                "Empty Document",
                emptySummary);
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var maskedLines = new string[lines.Length];
        bool inCodeFence = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                maskedLines[i] = new string(' ', line.Length);
                continue;
            }
            if (inCodeFence)
            {
                maskedLines[i] = new string(' ', line.Length);
                continue;
            }

            var chars = line.ToCharArray();
            bool inBacktick = false;
            for (int c = 0; c < chars.Length; c++)
            {
                if (chars[c] == '`')
                {
                    inBacktick = !inBacktick;
                    chars[c] = ' ';
                }
                else if (inBacktick)
                {
                    chars[c] = ' ';
                }
            }
            maskedLines[i] = new string(chars);
        }

        var defRegex = new Regex(@"^\s{0,3}\[\^([^\]]+)\]:\s*(.*)$");
        var refRegex = new Regex(@"(?<!\\)\[\^([^\]]+)\]");

        var definitions = new Dictionary<string, List<(int Line, string Text)>>(StringComparer.OrdinalIgnoreCase);
        var references = new List<(int Line, int Col, string Label)>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = maskedLines[i];
            var defMatch = defRegex.Match(line);
            if (defMatch.Success)
            {
                string label = defMatch.Groups[1].Value.Trim();
                if (!definitions.TryGetValue(label, out var list))
                {
                    list = new List<(int Line, string Text)>();
                    definitions[label] = list;
                }
                list.Add((i + 1, defMatch.Groups[2].Value));
            }
            else
            {
                var refMatches = refRegex.Matches(line);
                foreach (Match rm in refMatches)
                {
                    string label = rm.Groups[1].Value.Trim();
                    references.Add((i + 1, rm.Index + 1, label));
                }
            }
        }

        var items = new List<MarkdownFootnoteSequenceItem>();
        int totalRefs = references.Count;
        int totalDefs = definitions.Values.Sum(v => v.Count);
        int matchedPairs = 0;
        int orphanRefs = 0;
        int orphanDefs = 0;
        int seqMismatches = 0;
        int expectedNumericSeq = 1;

        foreach (var r in references)
        {
            bool hasDef = definitions.ContainsKey(r.Label);
            bool isNum = int.TryParse(r.Label, out int numVal);
            bool inSeq = true;

            if (isNum)
            {
                if (numVal == expectedNumericSeq)
                {
                    expectedNumericSeq++;
                }
                else
                {
                    inSeq = false;
                    seqMismatches++;
                }
            }

            if (hasDef) matchedPairs++;
            else orphanRefs++;

            string status = !hasDef
                ? $"Orphaned Footnote Reference [^{r.Label}] (Missing definition [^{r.Label}]: ...)"
                : !inSeq
                    ? $"Out-of-Sequence Numeric Footnote [^{r.Label}] (Expected [^{expectedNumericSeq - (numVal > expectedNumericSeq ? 0 : 1)}])"
                    : $"Valid Footnote Reference [^{r.Label}] (Paired with definition)";

            items.Add(new MarkdownFootnoteSequenceItem(
                r.Line, r.Col, r.Label, true, false, hasDef, isNum, isNum ? numVal : 0, inSeq, status));
        }

        var referencedLabels = references.Select(r => r.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in definitions)
        {
            string label = kvp.Key;
            bool isReferenced = referencedLabels.Contains(label);
            bool isNum = int.TryParse(label, out int numVal);

            foreach (var defEntry in kvp.Value)
            {
                if (!isReferenced) orphanDefs++;

                string status = !isReferenced
                    ? $"Orphaned Footnote Definition [^{label}]: (No inline reference [^{label}] found)"
                    : $"Valid Footnote Definition [^{label}]: (Referenced in document)";

                items.Add(new MarkdownFootnoteSequenceItem(
                    defEntry.Line, 1, label, false, true, isReferenced, isNum, isNum ? numVal : 0, true, status));
            }
        }

        double score = 100.0;
        score -= orphanRefs * 20.0;
        score -= orphanDefs * 15.0;
        score -= seqMismatches * 10.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (totalRefs == 0 && totalDefs == 0) category = "No Footnotes";
        else if (score >= 90.0) category = "Optimal Footnote Sequence & Definition Governance (≥90%)";
        else if (score >= 75.0) category = "Good Footnote Pairing with Minor Sequence Gaps (75-89%)";
        else if (score >= 50.0) category = "Moderate Footnote Sequence or Definition Discrepancies (50-74%)";
        else category = "Severe Orphaned Footnote Anchor or Definition Corruption (<50%)";

        string summary = FormatFootnoteReferencesSummary(
            totalRefs, totalDefs, matchedPairs, orphanRefs, orphanDefs, seqMismatches, score, category);

        return new MarkdownFootnoteAudit(
            totalRefs, totalDefs, matchedPairs, orphanRefs, orphanDefs, seqMismatches, score, items, category, summary);
    }

    public static string FormatFootnoteReferencesSummary(string? markdownText)
        => AuditFootnoteReferencesAndDefinitions(markdownText).Summary;

    public static string FormatFootnoteReferencesSummary(
        int totalRefs, int totalDefs, int matched, int orphanRefs, int orphanDefs, int seqMismatches, double score, string category)
    {
        if (totalRefs == 0 && totalDefs == 0)
            return "Markdown footnote audit: Clean document · 0 footnote references or definitions detected";

        var parts = new List<string>();
        parts.Add($"{totalRefs} ref{(totalRefs == 1 ? "" : "s")}");
        parts.Add($"{totalDefs} def{(totalDefs == 1 ? "" : "s")}");
        if (matched > 0) parts.Add($"{matched} matched");
        if (orphanRefs > 0) parts.Add($"⚠ {orphanRefs} orphaned ref{(orphanRefs == 1 ? "" : "s")}");
        if (orphanDefs > 0) parts.Add($"⚠ {orphanDefs} unreferenced def{(orphanDefs == 1 ? "" : "s")}");
        if (seqMismatches > 0) parts.Add($"⚠ {seqMismatches} sequence mismatch{(seqMismatches == 1 ? "" : "es")}");

        string details = string.Join(" · ", parts);
        return $"Markdown footnote audit ({details} · {category} · {score:0.#}% integrity score)";
    }
    public static MarkdownListNestingAudit AuditListNestingAndMarkerSequence(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown list audit: Clean document · 0 list items detected";
            return new MarkdownListNestingAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownListNestingItem>(),
                "Empty Document",
                emptySummary);
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var maskedLines = new string[lines.Length];
        bool inCodeFence = false;
        bool inFrontmatter = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (i == 0 && (trimmed == "---" || trimmed == "+++"))
            {
                inFrontmatter = true;
                maskedLines[i] = string.Empty;
                continue;
            }
            if (inFrontmatter)
            {
                if (trimmed == "---" || trimmed == "+++")
                {
                    inFrontmatter = false;
                }
                maskedLines[i] = string.Empty;
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                maskedLines[i] = string.Empty;
                continue;
            }
            if (inCodeFence)
            {
                maskedLines[i] = string.Empty;
                continue;
            }

            // Exclude thematic breaks (e.g. ---, ***, ___, - - -, * * *)
            if (Regex.IsMatch(trimmed, @"^(?:[-*_]\s*){3,}$"))
            {
                maskedLines[i] = string.Empty;
                continue;
            }

            maskedLines[i] = line;
        }

        var items = new List<MarkdownListNestingItem>();
        int unorderedCount = 0;
        int orderedCount = 0;
        int maxDepth = 0;
        int indentAnomalyCount = 0;
        int markerInconsistencyCount = 0;
        int sequenceMismatchCount = 0;

        var activeUnorderedMarkers = new Dictionary<int, string>();
        var expectedOrderedNumbers = new Dictionary<int, int>();
        var activeListType = new Dictionary<int, bool>(); // true = ordered, false = unordered

        var unorderedRegex = new Regex(@"^([-*+])(\s+)(.*)$");
        var orderedRegex = new Regex(@"^(\d+)([.)])(\s+)(.*)$");

        int blankLineCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = maskedLines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                blankLineCount++;
                if (blankLineCount >= 2)
                {
                    // Consecutive blank lines terminate the active list block
                    activeUnorderedMarkers.Clear();
                    expectedOrderedNumbers.Clear();
                    activeListType.Clear();
                }
                continue;
            }

            blankLineCount = 0;

            int leadingSpaces = 0;
            for (int c = 0; c < line.Length; c++)
            {
                if (line[c] == ' ') leadingSpaces++;
                else if (line[c] == '\t') leadingSpaces += 4;
                else break;
            }

            string trimmed = line.TrimStart();
            var unorderMatch = unorderedRegex.Match(trimmed);
            var orderMatch = orderedRegex.Match(trimmed);

            if (!unorderMatch.Success && !orderMatch.Success)
            {
                // Non-list line (e.g. heading, paragraph) resets the active list block
                activeUnorderedMarkers.Clear();
                expectedOrderedNumbers.Clear();
                activeListType.Clear();
                continue;
            }

            int nestingLevel = leadingSpaces / 2;
            if (nestingLevel > maxDepth) maxDepth = nestingLevel;

            // Clear any deeper level state
            var keysToRemove = activeUnorderedMarkers.Keys.Where(k => k > nestingLevel).ToList();
            foreach (var k in keysToRemove)
            {
                activeUnorderedMarkers.Remove(k);
                expectedOrderedNumbers.Remove(k);
                activeListType.Remove(k);
            }

            bool isIndentAnomaly = leadingSpaces % 2 != 0;
            if (isIndentAnomaly) indentAnomalyCount++;

            if (unorderMatch.Success)
            {
                unorderedCount++;
                string marker = unorderMatch.Groups[1].Value;
                string content = unorderMatch.Groups[3].Value.Trim();

                bool markerInconsistent = false;
                if (activeListType.TryGetValue(nestingLevel, out bool prevIsOrdered) && prevIsOrdered)
                {
                    markerInconsistent = true;
                    markerInconsistencyCount++;
                }
                else if (activeUnorderedMarkers.TryGetValue(nestingLevel, out string? activeMarker) && activeMarker != marker)
                {
                    markerInconsistent = true;
                    markerInconsistencyCount++;
                }
                else
                {
                    activeUnorderedMarkers[nestingLevel] = marker;
                }

                activeListType[nestingLevel] = false;
                expectedOrderedNumbers.Remove(nestingLevel);

                string status = markerInconsistent
                    ? $"Marker Inconsistency: Level {nestingLevel} mixed '{marker}' marker"
                    : isIndentAnomaly
                        ? $"Indentation Anomaly: {leadingSpaces} spaces (odd indent)"
                        : $"Valid Unordered Item: Level {nestingLevel} '{marker}'";

                items.Add(new MarkdownListNestingItem(
                    i + 1, leadingSpaces, nestingLevel, false, marker, content,
                    markerInconsistent, isIndentAnomaly, false, status));
            }
            else if (orderMatch.Success)
            {
                orderedCount++;
                string numStr = orderMatch.Groups[1].Value;
                string delim = orderMatch.Groups[2].Value;
                string marker = numStr + delim;
                string content = orderMatch.Groups[4].Value.Trim();
                int numVal = int.TryParse(numStr, out int parsed) ? parsed : 1;

                bool seqMismatch = false;
                bool markerInconsistent = false;

                if (activeListType.TryGetValue(nestingLevel, out bool prevIsOrdered) && !prevIsOrdered)
                {
                    markerInconsistent = true;
                    markerInconsistencyCount++;
                }

                if (expectedOrderedNumbers.TryGetValue(nestingLevel, out int expectedNum))
                {
                    if (numVal != expectedNum)
                    {
                        seqMismatch = true;
                        sequenceMismatchCount++;
                    }
                    expectedOrderedNumbers[nestingLevel] = numVal + 1;
                }
                else
                {
                    expectedOrderedNumbers[nestingLevel] = numVal + 1;
                }

                activeListType[nestingLevel] = true;
                activeUnorderedMarkers.Remove(nestingLevel);

                string status = seqMismatch
                    ? $"Sequence Mismatch: Expected {expectedOrderedNumbers[nestingLevel] - 1}, found {numVal}"
                    : markerInconsistent
                        ? $"Marker Inconsistency: Ordered item following unordered at Level {nestingLevel}"
                        : isIndentAnomaly
                            ? $"Indentation Anomaly: {leadingSpaces} spaces (odd indent)"
                            : $"Valid Ordered Item: Level {nestingLevel} #{numVal}";

                items.Add(new MarkdownListNestingItem(
                    i + 1, leadingSpaces, nestingLevel, true, marker, content,
                    markerInconsistent, isIndentAnomaly, seqMismatch, status));
            }
        }

        int totalListItems = items.Count;
        double score = 100.0;
        score -= indentAnomalyCount * 5.0;
        score -= markerInconsistencyCount * 8.0;
        score -= sequenceMismatchCount * 10.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (totalListItems == 0) category = "No List Items";
        else if (score >= 90.0) category = "Optimal List Structure & Marker Hierarchy (≥90%)";
        else if (score >= 75.0) category = "Good List Nesting with Minor Formatting Inconsistencies (75-89%)";
        else if (score >= 50.0) category = "Moderate List Indentation or Sequence Discrepancies (50-74%)";
        else category = "Severe List Hierarchy & Numbering Sequence Defects (<50%)";

        string summary = FormatListNestingSummary(
            totalListItems, unorderedCount, orderedCount, maxDepth,
            indentAnomalyCount, markerInconsistencyCount, sequenceMismatchCount, score, category);

        return new MarkdownListNestingAudit(
            totalListItems, unorderedCount, orderedCount, maxDepth,
            indentAnomalyCount, markerInconsistencyCount, sequenceMismatchCount,
            score, items, category, summary);
    }

    public static string FormatListNestingSummary(string? markdownText)
        => AuditListNestingAndMarkerSequence(markdownText).Summary;

    public static string FormatListNestingSummary(
        int totalItems, int unordered, int ordered, int maxDepth,
        int indentAnomalies, int markerInconsistencies, int seqMismatches,
        double score, string category)
    {
        if (totalItems == 0)
            return "Markdown list audit: Clean document · 0 list items detected";

        var parts = new List<string>();
        parts.Add($"{totalItems} item{(totalItems == 1 ? "" : "s")}");
        if (unordered > 0) parts.Add($"{unordered} unordered");
        if (ordered > 0) parts.Add($"{ordered} ordered");
        if (maxDepth > 0) parts.Add($"depth {maxDepth}");
        if (indentAnomalies > 0) parts.Add($"⚠️ {indentAnomalies} indent anomal{(indentAnomalies == 1 ? "y" : "ies")}");
        if (markerInconsistencies > 0) parts.Add($"⚠️ {markerInconsistencies} marker mismatch{(markerInconsistencies == 1 ? "" : "es")}");
        if (seqMismatches > 0) parts.Add($"⚠️ {seqMismatches} sequence jump{(seqMismatches == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown list audit ({details} · {category} · {score:0.#}% consistency score)";
    }

    public static MarkdownInlineHtmlSecurityAudit AuditMarkdownInlineHtmlSecurity(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatInlineHtmlSecuritySummary(0, 0, 0, 0, 0, 100.0, "Clean Document");
            return new MarkdownInlineHtmlSecurityAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownInlineHtmlSecurityItem>(),
                "Clean Document",
                emptySummary);
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var maskedLines = new string[lines.Length];
        bool inCodeFence = false;
        bool inFrontmatter = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (i == 0 && (trimmed == "---" || trimmed == "+++"))
            {
                inFrontmatter = true;
                maskedLines[i] = new string(' ', line.Length);
                continue;
            }
            if (inFrontmatter)
            {
                maskedLines[i] = new string(' ', line.Length);
                if (trimmed == "---" || trimmed == "+++")
                {
                    inFrontmatter = false;
                }
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                maskedLines[i] = new string(' ', line.Length);
                continue;
            }

            if (inCodeFence)
            {
                maskedLines[i] = new string(' ', line.Length);
                continue;
            }

            // Mask inline code spans `...`
            var chars = line.ToCharArray();
            bool inBacktick = false;
            for (int c = 0; c < chars.Length; c++)
            {
                if (chars[c] == '`')
                {
                    inBacktick = !inBacktick;
                    chars[c] = ' ';
                }
                else if (inBacktick)
                {
                    chars[c] = ' ';
                }
            }

            maskedLines[i] = new string(chars);
        }

        var items = new List<MarkdownInlineHtmlSecurityItem>();
        int totalTags = 0;
        int dangerousTagCount = 0;
        int dangerousAttrCount = 0;
        int safeTagCount = 0;
        int unclosedTagCount = 0;

        var dangerousTagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script", "iframe", "object", "embed", "style", "form", "input",
            "button", "meta", "link", "base", "applet"
        };

        var dangerousAttrPatterns = new[]
        {
            @"on[a-zA-Z]+\s*=",
            @"href\s*=\s*['""]?\s*(javascript|data|vbscript):",
            @"src\s*=\s*['""]?\s*(javascript|data|vbscript):"
        };

        var voidTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "br", "hr", "img", "input", "link", "meta", "area", "base", "col", "embed", "param", "source", "track", "wbr"
        };

        var openTagStack = new Stack<(string Tag, int Line)>();

        var tagRegex = new System.Text.RegularExpressions.Regex(
            @"<(/)?([a-zA-Z0-9_\-]+)([^>]*)>",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        for (int l = 0; l < maskedLines.Length; l++)
        {
            string line = maskedLines[l];
            var matches = tagRegex.Matches(line);

            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                totalTags++;
                bool isClosing = match.Groups[1].Value == "/";
                string tagName = match.Groups[2].Value.ToLowerInvariant();
                string rawTag = match.Value;
                string attributesStr = match.Groups[3].Value;
                int column = match.Index + 1;

                bool isDangerousTag = dangerousTagNames.Contains(tagName);
                var dangerousAttrs = new List<string>();

                if (!isClosing && !string.IsNullOrWhiteSpace(attributesStr))
                {
                    foreach (var pattern in dangerousAttrPatterns)
                    {
                        var attrMatches = System.Text.RegularExpressions.Regex.Matches(
                            attributesStr, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        foreach (System.Text.RegularExpressions.Match am in attrMatches)
                        {
                            dangerousAttrs.Add(am.Value.Trim());
                        }
                    }
                }

                bool hasDangerousAttrs = dangerousAttrs.Count > 0;
                if (isDangerousTag) dangerousTagCount++;
                if (hasDangerousAttrs) dangerousAttrCount += dangerousAttrs.Count;
                if (!isDangerousTag && !hasDangerousAttrs) safeTagCount++;

                string riskLevel = "Safe";
                string recommendation = "Tag conforms to safe inline Markdown HTML usage";
                string status = $"<{tagName}> tag";

                if (isDangerousTag)
                {
                    riskLevel = "Critical";
                    recommendation = $"Remove or escape <{tagName}> tag to eliminate script injection and DOM corruption hazards";
                    status = $"Critical hazard: dangerous <{tagName}> tag detected";
                }
                else if (hasDangerousAttrs)
                {
                    riskLevel = "High";
                    recommendation = $"Remove dangerous event handler or protocol attributes ({string.Join(", ", dangerousAttrs)})";
                    status = $"High risk: dangerous attribute in <{tagName}> tag";
                }

                if (!voidTags.Contains(tagName))
                {
                    if (isClosing)
                    {
                        if (openTagStack.Count > 0 && string.Equals(openTagStack.Peek().Tag, tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            openTagStack.Pop();
                        }
                    }
                    else if (!rawTag.TrimEnd().EndsWith("/>"))
                    {
                        openTagStack.Push((tagName, l + 1));
                    }
                }

                items.Add(new MarkdownInlineHtmlSecurityItem(
                    l + 1, column, tagName, rawTag, isClosing, isDangerousTag,
                    hasDangerousAttrs, dangerousAttrs, riskLevel, recommendation, status));
            }
        }

        unclosedTagCount = openTagStack.Count;

        double score = 100.0;
        score -= dangerousTagCount * 30.0;
        score -= dangerousAttrCount * 20.0;
        score -= unclosedTagCount * 5.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (totalTags == 0) category = "Clean Document";
        else if (score >= 95.0) category = "Pristine Sanitization (≥95%)";
        else if (score >= 80.0) category = "Low Injection Risk (80-94%)";
        else if (score >= 60.0) category = "Moderate Injection Risk (60-79%)";
        else if (score >= 40.0) category = "High Injection Risk (40-59%)";
        else category = "Critical Security Hazard (<40%)";

        string summary = FormatInlineHtmlSecuritySummary(
            totalTags, dangerousTagCount, dangerousAttrCount, safeTagCount, unclosedTagCount, score, category);

        return new MarkdownInlineHtmlSecurityAudit(
            totalTags, dangerousTagCount, dangerousAttrCount, safeTagCount, unclosedTagCount,
            score, items, category, summary);
    }

    public static string FormatInlineHtmlSecuritySummary(string? markdownText)
        => AuditMarkdownInlineHtmlSecurity(markdownText).Summary;

    public static string FormatInlineHtmlSecuritySummary(
        int totalTags, int dangerousTags, int dangerousAttrs, int safeTags,
        int unclosedTags, double score, string category)
    {
        if (totalTags == 0)
            return "Markdown inline HTML security audit: Clean document · 0 HTML tags detected";

        string tagStr = totalTags == 1 ? "1 HTML tag" : $"{totalTags} HTML tags";
        var parts = new List<string>();

        parts.Add(tagStr);
        if (safeTags > 0) parts.Add($"{safeTags} safe");
        if (dangerousTags > 0) parts.Add($"⚠️ {dangerousTags} dangerous tag{(dangerousTags == 1 ? "" : "s")}");
        if (dangerousAttrs > 0) parts.Add($"⚠️ {dangerousAttrs} dangerous attr{(dangerousAttrs == 1 ? "" : "s")}");
        if (unclosedTags > 0) parts.Add($"⚠️ {unclosedTags} unclosed tag{(unclosedTags == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown inline HTML security audit ({details} · {category} · {score:0.#}% security score)";
    }

    public static MarkdownTaskListProgressionAudit AuditMarkdownTaskListProgression(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatTaskListProgressionSummary(0, 0, 0, 0, 0, 100.0, "No Task Lists");
            return new MarkdownTaskListProgressionAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<MarkdownTaskProgressionItem>(),
                "No Task Lists",
                emptySummary);
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var maskedLines = new string[lines.Length];
        bool inCodeFence = false;
        bool inFrontmatter = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (i == 0 && (trimmed == "---" || trimmed == "+++"))
            {
                inFrontmatter = true;
                maskedLines[i] = string.Empty;
                continue;
            }
            if (inFrontmatter)
            {
                maskedLines[i] = string.Empty;
                if (trimmed == "---" || trimmed == "+++")
                {
                    inFrontmatter = false;
                }
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                maskedLines[i] = string.Empty;
                continue;
            }

            if (inCodeFence)
            {
                maskedLines[i] = string.Empty;
                continue;
            }

            maskedLines[i] = line;
        }

        var items = new List<MarkdownTaskProgressionItem>();
        int totalTasks = 0;
        int completedTasks = 0;
        int pendingTasks = 0;
        int emptyTaskCount = 0;
        int maxNestingDepth = 0;
        int previousIndent = 0;
        int indentationJumpCount = 0;

        var taskRegex = new System.Text.RegularExpressions.Regex(
            @"^(?<indent>\s*)(?<marker>[-*+]|\d+\.)\s+\[(?<state>[ xX])\](?:\s+(?<desc>.*))?$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        for (int l = 0; l < maskedLines.Length; l++)
        {
            string line = maskedLines[l];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var match = taskRegex.Match(line);
            if (!match.Success) continue;

            totalTasks++;
            string indentStr = match.Groups["indent"].Value;
            int indentSpaces = 0;
            foreach (char c in indentStr)
            {
                indentSpaces += (c == '\t') ? 4 : 1;
            }

            int nestingLevel = indentSpaces / 2;
            if (nestingLevel > maxNestingDepth) maxNestingDepth = nestingLevel;

            bool isCompleted = match.Groups["state"].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
            if (isCompleted) completedTasks++; else pendingTasks++;

            string marker = match.Groups["marker"].Value;
            string description = match.Groups["desc"].Value?.Trim() ?? string.Empty;
            bool isEmpty = string.IsNullOrWhiteSpace(description);
            if (isEmpty) emptyTaskCount++;

            bool hasIndentationJump = false;
            if (items.Count > 0 && indentSpaces > previousIndent + 4)
            {
                hasIndentationJump = true;
                indentationJumpCount++;
            }
            previousIndent = indentSpaces;

            string status = isCompleted ? "Completed" : "Pending";
            string recommendation = "Task item properly formatted";
            if (isEmpty)
            {
                recommendation = "Add a task title or description to give context to this checkbox";
            }
            else if (hasIndentationJump)
            {
                recommendation = "Adjust indentation: nesting jump exceeds 4 spaces relative to predecessor";
            }

            items.Add(new MarkdownTaskProgressionItem(
                l + 1, indentSpaces, nestingLevel, isCompleted, marker, description,
                isEmpty, hasIndentationJump, status, recommendation));
        }

        if (totalTasks == 0)
        {
            string emptySummary = FormatTaskListProgressionSummary(0, 0, 0, 0, 0, 100.0, "No Task Lists");
            return new MarkdownTaskListProgressionAudit(
                0, 0, 0, 0, 0, 0.0, 100.0, items, "No Task Lists", emptySummary);
        }

        double completionRatio = (double)completedTasks / totalTasks;
        double score = 100.0;
        score -= emptyTaskCount * 15.0;
        score -= indentationJumpCount * 10.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (score >= 95.0) category = "Flawless Task Hygiene (≥95%)";
        else if (score >= 80.0) category = "High Task Hygiene (80-94%)";
        else if (score >= 60.0) category = "Moderate Task Hygiene (60-79%)";
        else if (score >= 40.0) category = "Suboptimal Task Tracking (40-59%)";
        else category = "Poor Task Structure (<40%)";

        string summary = FormatTaskListProgressionSummary(
            totalTasks, completedTasks, pendingTasks, emptyTaskCount, maxNestingDepth, score, category);

        return new MarkdownTaskListProgressionAudit(
            totalTasks, completedTasks, pendingTasks, emptyTaskCount, maxNestingDepth,
            completionRatio, score, items, category, summary);
    }

    public static MarkdownLinkReferenceAudit AuditMarkdownLinkReferenceDefinitions(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatLinkReferenceSummary(0, 0, 0, 0, 0, 0, 100.0, "No Reference Links");
            return new MarkdownLinkReferenceAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownLinkReferenceItem>(),
                "No Reference Links",
                emptySummary);
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var maskedLines = new string[lines.Length];
        bool inCodeFence = false;
        bool inFrontmatter = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (i == 0 && (trimmed == "---" || trimmed == "+++"))
            {
                inFrontmatter = true;
                maskedLines[i] = string.Empty;
                continue;
            }
            if (inFrontmatter)
            {
                maskedLines[i] = string.Empty;
                if (trimmed == "---" || trimmed == "+++")
                {
                    inFrontmatter = false;
                }
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                maskedLines[i] = string.Empty;
                continue;
            }

            if (inCodeFence)
            {
                maskedLines[i] = string.Empty;
                continue;
            }

            maskedLines[i] = line;
        }

        var defRegex = new System.Text.RegularExpressions.Regex(
            @"^\s{0,3}\[(?<label>[^\]]+)\]:\s*(?:<(?<dest>[^>]+)>|(?<dest>\S+))(?:\s+(?:""(?<title>[^""]*)""|'(?<title>[^']*)'|\((?<title>[^)]*)\)))?\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var definitions = new Dictionary<string, (int Line, int Col, string Label, string Dest, string Title)>(StringComparer.OrdinalIgnoreCase);
        var duplicateDefs = new List<(int Line, int Col, string Label, string Dest, string Title)>();
        var items = new List<MarkdownLinkReferenceItem>();

        for (int l = 0; l < maskedLines.Length; l++)
        {
            string line = maskedLines[l];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var match = defRegex.Match(line);
            if (!match.Success) continue;

            string label = match.Groups["label"].Value.Trim();
            string dest = match.Groups["dest"].Value.Trim();
            string title = match.Groups["title"].Value?.Trim() ?? string.Empty;
            int col = match.Groups["label"].Index + 1;

            if (definitions.ContainsKey(label))
            {
                duplicateDefs.Add((l + 1, col, label, dest, title));
            }
            else
            {
                definitions[label] = (l + 1, col, label, dest, title);
            }
        }

        var fullOrCollapsedRegex = new System.Text.RegularExpressions.Regex(
            @"(?<!\!)\[(?<text>(?:[^\[\]\\]|\\.)+)\]\[(?<ref>[^\]]*)\]",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var shortcutRegex = new System.Text.RegularExpressions.Regex(
            @"(?<![!\[\^])\[(?<label>[a-zA-Z0-9_\-\.]+)(?<!\^)\](?![\[\(:\^])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var referencedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int totalUsages = 0;
        int resolvedCount = 0;
        int unresolvedCount = 0;

        for (int l = 0; l < maskedLines.Length; l++)
        {
            string line = maskedLines[l];
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (defRegex.IsMatch(line)) continue;

            var fullMatches = fullOrCollapsedRegex.Matches(line);
            var matchedSpans = new List<(int Start, int End)>();

            foreach (System.Text.RegularExpressions.Match m in fullMatches)
            {
                totalUsages++;
                matchedSpans.Add((m.Index, m.Index + m.Length));
                string text = m.Groups["text"].Value;
                string refKey = m.Groups["ref"].Value.Trim();
                bool isCollapsed = string.IsNullOrEmpty(refKey);
                string targetLabel = isCollapsed ? text.Trim() : refKey;
                string usageType = isCollapsed ? "Collapsed" : "Full";

                bool isResolved = definitions.TryGetValue(targetLabel, out var def);
                if (isResolved)
                {
                    resolvedCount++;
                    referencedLabels.Add(targetLabel);
                    items.Add(new MarkdownLinkReferenceItem(
                        l + 1, m.Index + 1, targetLabel, def.Dest, def.Title,
                        IsDefinition: false, IsUsage: true, usageType, IsResolved: true,
                        IsUnreferenced: false, IsDuplicateDefinition: false,
                        "Resolved", $"Resolves to destination '{def.Dest}'"));
                }
                else
                {
                    unresolvedCount++;
                    items.Add(new MarkdownLinkReferenceItem(
                        l + 1, m.Index + 1, targetLabel, string.Empty, string.Empty,
                        IsDefinition: false, IsUsage: true, usageType, IsResolved: false,
                        IsUnreferenced: false, IsDuplicateDefinition: false,
                        "Unresolved", $"Missing link reference definition: add '[{targetLabel}]: url' to document"));
                }
            }

            var shortcutMatches = shortcutRegex.Matches(line);
            foreach (System.Text.RegularExpressions.Match m in shortcutMatches)
            {
                bool overlaps = matchedSpans.Any(s => m.Index >= s.Start && m.Index < s.End);
                if (overlaps) continue;

                string targetLabel = m.Groups["label"].Value.Trim();
                if (definitions.TryGetValue(targetLabel, out var def))
                {
                    totalUsages++;
                    resolvedCount++;
                    referencedLabels.Add(targetLabel);
                    items.Add(new MarkdownLinkReferenceItem(
                        l + 1, m.Index + 1, targetLabel, def.Dest, def.Title,
                        IsDefinition: false, IsUsage: true, "Shortcut", IsResolved: true,
                        IsUnreferenced: false, IsDuplicateDefinition: false,
                        "Resolved", $"Shortcut link resolves to destination '{def.Dest}'"));
                }
            }
        }

        int unreferencedDefs = 0;
        foreach (var kvp in definitions)
        {
            var def = kvp.Value;
            bool isReferenced = referencedLabels.Contains(kvp.Key);
            if (!isReferenced) unreferencedDefs++;

            string status = isReferenced ? "Active" : "Unreferenced";
            string recommendation = isReferenced
                ? "Definition actively referenced"
                : $"Unreferenced definition: remove '[{def.Label}]' or reference it in text with '[text][{def.Label}]'";

            items.Add(new MarkdownLinkReferenceItem(
                def.Line, def.Col, def.Label, def.Dest, def.Title,
                IsDefinition: true, IsUsage: false, "Definition", IsResolved: true,
                IsUnreferenced: !isReferenced, IsDuplicateDefinition: false,
                status, recommendation));
        }

        foreach (var dup in duplicateDefs)
        {
            items.Add(new MarkdownLinkReferenceItem(
                dup.Line, dup.Col, dup.Label, dup.Dest, dup.Title,
                IsDefinition: true, IsUsage: false, "Definition", IsResolved: false,
                IsUnreferenced: false, IsDuplicateDefinition: true,
                "Duplicate", $"Duplicate definition of '[{dup.Label}]': earlier definition takes precedence"));
        }

        int totalDefs = definitions.Count + duplicateDefs.Count;
        if (totalDefs == 0 && totalUsages == 0)
        {
            string cleanSummary = FormatLinkReferenceSummary(0, 0, 0, 0, 0, 0, 100.0, "No Reference Links");
            return new MarkdownLinkReferenceAudit(
                0, 0, 0, 0, 0, 0, 100.0, items, "No Reference Links", cleanSummary);
        }

        double score = 100.0;
        score -= unresolvedCount * 20.0;
        score -= unreferencedDefs * 5.0;
        score -= duplicateDefs.Count * 10.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (score >= 95.0) category = "Flawless Link Reference Integrity (≥95%)";
        else if (score >= 80.0) category = "High Reference Resolution (80-94%)";
        else if (score >= 60.0) category = "Moderate Reference Linkage (60-79%)";
        else if (score >= 40.0) category = "Suboptimal Reference Resolution (40-59%)";
        else category = "Poor Reference Linkage (<40%)";

        string summary = FormatLinkReferenceSummary(
            totalDefs, totalUsages, resolvedCount, unresolvedCount, unreferencedDefs, duplicateDefs.Count, score, category);

        return new MarkdownLinkReferenceAudit(
            totalDefs, totalUsages, resolvedCount, unresolvedCount, unreferencedDefs, duplicateDefs.Count,
            score, items, category, summary);
    }

    public static string FormatLinkReferenceSummary(string? markdownText)
        => AuditMarkdownLinkReferenceDefinitions(markdownText).Summary;

    public static string FormatLinkReferenceSummary(
        int totalDefs, int totalUsages, int resolved, int unresolved,
        int unreferenced, int duplicates, double score, string category)
    {
        if (totalDefs == 0 && totalUsages == 0)
            return "Markdown link reference audit: Clean document · 0 reference links detected";

        var parts = new List<string>
        {
            $"{totalDefs} defs",
            $"{totalUsages} usages ({resolved} resolved)"
        };
        if (unresolved > 0) parts.Add($"⚠️ {unresolved} unresolved");
        if (unreferenced > 0) parts.Add($"{unreferenced} unreferenced");
        if (duplicates > 0) parts.Add($"{duplicates} duplicates");

        string details = string.Join(" · ", parts);
        return $"Markdown link reference audit ({details} · {category} · {score:0.#}% resolution score)";
    }


    public static string FormatTaskListProgressionSummary(string? markdownText)
        => AuditMarkdownTaskListProgression(markdownText).Summary;

    public static string FormatTaskListProgressionSummary(
        int totalTasks, int completedTasks, int pendingTasks, int emptyTasks,
        int maxNesting, double score, string category)
    {
        if (totalTasks == 0)
            return "Markdown task list audit: Clean document · 0 task checkboxes detected";

        double pct = (double)completedTasks / totalTasks * 100.0;
        string taskStr = totalTasks == 1 ? "1 task" : $"{totalTasks} tasks";
        var parts = new List<string>
        {
            taskStr,
            $"{completedTasks} completed ({pct:0.#}%)",
            $"{pendingTasks} pending"
        };
        if (emptyTasks > 0) parts.Add($"⚠️ {emptyTasks} unlabeled");
        if (maxNesting > 0) parts.Add($"depth {maxNesting}");

        string details = string.Join(" · ", parts);
        return $"Markdown task list audit ({details} · {category} · {score:0.#}% hygiene score)";
    }

    public static MarkdownFootnoteDefinitionAudit AuditMarkdownFootnoteDefinitions(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatFootnoteDefinitionSummary(0, 0, 0, 0, 0, 0, 100.0, "No Footnotes");
            return new MarkdownFootnoteDefinitionAudit(
                0, 0, 0, 0, 0, 0, 0, true, 100.0,
                Array.Empty<MarkdownFootnoteDefinitionItem>(),
                "No Footnotes",
                emptySummary);
        }

        var lines = markdownText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var maskedLines = new string[lines.Length];
        bool inCodeFence = false;
        bool inFrontmatter = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (i == 0 && (trimmed == "---" || trimmed == "+++"))
            {
                inFrontmatter = true;
                maskedLines[i] = string.Empty;
                continue;
            }
            if (inFrontmatter)
            {
                maskedLines[i] = string.Empty;
                if (trimmed == "---" || trimmed == "+++")
                    inFrontmatter = false;
                continue;
            }

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                maskedLines[i] = string.Empty;
                continue;
            }
            if (inCodeFence)
            {
                maskedLines[i] = string.Empty;
                continue;
            }

            maskedLines[i] = line;
        }

        var defRegex = new System.Text.RegularExpressions.Regex(@"^[ ]{0,3}\[\^([^\]]+)\]:\s*(.*)$");
        var calloutRegex = new System.Text.RegularExpressions.Regex(@"\[\^([^\]]+)\]");

        var defDict = new Dictionary<string, List<(int Line, string Excerpt)>>(StringComparer.OrdinalIgnoreCase);
        var calloutList = new List<(string Id, int Line, int Col)>();
        var items = new List<MarkdownFootnoteDefinitionItem>();

        var defLineIndices = new HashSet<int>();

        for (int i = 0; i < maskedLines.Length; i++)
        {
            string line = maskedLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var defMatch = defRegex.Match(line);
            if (defMatch.Success)
            {
                defLineIndices.Add(i);
                string id = defMatch.Groups[1].Value.Trim();
                string excerpt = defMatch.Groups[2].Value.Trim();
                if (!defDict.TryGetValue(id, out var list))
                {
                    list = new List<(int Line, string Excerpt)>();
                    defDict[id] = list;
                }
                list.Add((i + 1, excerpt));
            }
        }

        for (int i = 0; i < maskedLines.Length; i++)
        {
            if (defLineIndices.Contains(i)) continue;
            string line = maskedLines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var matches = calloutRegex.Matches(line);
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string id = m.Groups[1].Value.Trim();
                calloutList.Add((id, i + 1, m.Index + 1));
            }
        }

        var referencedDefIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int resolvedCallouts = 0;
        int unresolvedCallouts = 0;
        int numericCount = 0;
        var numericCalloutValues = new List<int>();

        foreach (var c in calloutList)
        {
            bool hasDef = defDict.ContainsKey(c.Id);
            bool isNum = int.TryParse(c.Id, out int numVal);
            if (isNum)
            {
                numericCount++;
                numericCalloutValues.Add(numVal);
            }

            if (hasDef)
            {
                resolvedCallouts++;
                referencedDefIds.Add(c.Id);
                items.Add(new MarkdownFootnoteDefinitionItem(
                    c.Line, c.Col, c.Id,
                    IsDefinition: false, IsCallout: true, IsResolved: true,
                    IsOrphan: false, IsDuplicate: false, IsNumeric: isNum, NumericValue: numVal,
                    Excerpt: $"Callout [^{c.Id}]", Status: "Resolved", Recommendation: "Valid footnote callout"));
            }
            else
            {
                unresolvedCallouts++;
                items.Add(new MarkdownFootnoteDefinitionItem(
                    c.Line, c.Col, c.Id,
                    IsDefinition: false, IsCallout: true, IsResolved: false,
                    IsOrphan: false, IsDuplicate: false, IsNumeric: isNum, NumericValue: numVal,
                    Excerpt: $"Callout [^{c.Id}]", Status: "Missing Definition",
                    Recommendation: $"Add definition [^{c.Id}]: ... at the bottom of the document"));
            }
        }

        int totalDefs = 0;
        int orphanDefs = 0;
        int duplicateDefs = 0;

        foreach (var kvp in defDict)
        {
            string id = kvp.Key;
            var occurrences = kvp.Value;
            bool isReferenced = referencedDefIds.Contains(id);
            bool isNum = int.TryParse(id, out int numVal);

            for (int k = 0; k < occurrences.Count; k++)
            {
                totalDefs++;
                var occ = occurrences[k];
                bool isDup = k > 0;
                if (isDup) duplicateDefs++;
                if (!isReferenced) orphanDefs++;

                string status = isDup ? "Duplicate Definition" : (!isReferenced ? "Orphan Definition" : "Valid Definition");
                string rec = isDup
                    ? $"Consolidate or remove duplicate definition for [^{id}]"
                    : (!isReferenced ? $"Add callout [^{id}] in document body or remove unreferenced definition" : "Valid footnote definition");

                items.Add(new MarkdownFootnoteDefinitionItem(
                    occ.Line, 1, id,
                    IsDefinition: true, IsCallout: false, IsResolved: isReferenced,
                    IsOrphan: !isReferenced, IsDuplicate: isDup, IsNumeric: isNum, NumericValue: numVal,
                    Excerpt: occ.Excerpt, Status: status, Recommendation: rec));
            }
        }

        bool isMonotonic = true;
        for (int i = 1; i < numericCalloutValues.Count; i++)
        {
            if (numericCalloutValues[i] != numericCalloutValues[i - 1] + 1)
            {
                isMonotonic = false;
                break;
            }
        }

        double score = 100.0;
        score -= unresolvedCallouts * 20.0;
        score -= orphanDefs * 10.0;
        score -= duplicateDefs * 15.0;
        if (numericCalloutValues.Count > 1 && !isMonotonic)
            score -= 10.0;
        score = Math.Max(0.0, Math.Min(100.0, Math.Round(score, 1)));

        string category;
        if (totalDefs == 0 && calloutList.Count == 0) category = "No Footnotes";
        else if (score >= 95.0) category = "Pristine Footnote Linkage (≥95%)";
        else if (score >= 80.0) category = "Good Footnote Linkage (80-94%)";
        else if (score >= 60.0) category = "Marginal Footnote Linkage (60-79%)";
        else category = "Broken Footnote Linkage (<60%)";

        string summary = FormatFootnoteDefinitionSummary(
            totalDefs, calloutList.Count, resolvedCallouts, unresolvedCallouts, orphanDefs, duplicateDefs, score, category);

        return new MarkdownFootnoteDefinitionAudit(
            totalDefs, calloutList.Count, resolvedCallouts, unresolvedCallouts,
            orphanDefs, duplicateDefs, numericCount, isMonotonic, score,
            items, category, summary);
    }

    public static string FormatFootnoteDefinitionSummary(string? markdownText)
        => AuditMarkdownFootnoteDefinitions(markdownText).Summary;

    public static string FormatFootnoteDefinitionSummary(
        int totalDefs, int totalCallouts, int resolved, int unresolved,
        int orphans, int duplicates, double score, string category)
    {
        if (totalDefs == 0 && totalCallouts == 0)
            return "Markdown footnote definition audit: Clean document · 0 footnotes detected";

        var parts = new List<string>
        {
            $"{totalDefs} def{(totalDefs == 1 ? "" : "s")}",
            $"{totalCallouts} callout{(totalCallouts == 1 ? "" : "s")} ({resolved} resolved)"
        };
        if (unresolved > 0) parts.Add($"⚠️ {unresolved} unresolved");
        if (orphans > 0) parts.Add($"{orphans} orphan{(orphans == 1 ? "" : "s")}");
        if (duplicates > 0) parts.Add($"{duplicates} duplicate{(duplicates == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown footnote definition audit ({details} · {category} · {score:0.#}% integrity score)";
    }


    public static MarkdownMathDelimiterAudit AuditMarkdownMathDelimiters(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatMathDelimiterFidelitySummary(0, 0, 0, 0, 0, 0, 0, 100.0, "No Math Detected");
            return new MarkdownMathDelimiterAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownMathDelimiterItem>(),
                "No Math Detected",
                emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var items = new List<MarkdownMathDelimiterItem>();
        bool inFencedBlock = false;
        string? activeFence = null;

        int inlineCount = 0;
        int displayCount = 0;
        int validCount = 0;
        int unclosedCount = 0;
        int unescapedCurrencyCount = 0;
        int totalUnbalancedBraces = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                string fenceMarker = trimmed.Substring(0, 3);
                if (!inFencedBlock)
                {
                    inFencedBlock = true;
                    activeFence = fenceMarker;
                    continue;
                }
                else if (activeFence == fenceMarker)
                {
                    inFencedBlock = false;
                    activeFence = null;
                    continue;
                }
            }

            if (inFencedBlock) continue;

            var processedLine = Regex.Replace(line, @"`+[^`\n]+`+", m => new string(' ', m.Length));

            var latexBlockMatches = Regex.Matches(processedLine, @"\\\[(?<content>[\s\S]*?)\\\]");
            foreach (Match m in latexBlockMatches)
            {
                displayCount++;
                string content = m.Groups["content"].Value;
                int braces = CountUnbalancedBraces(content);
                bool valid = braces == 0;
                if (valid) validCount++;
                else totalUnbalancedBraces += Math.Abs(braces);

                items.Add(new MarkdownMathDelimiterItem(
                    i + 1, m.Index + 1, "Display LaTeX (\\[...\\])", content.Trim(),
                    true, braces == 0, false, false, Math.Abs(braces),
                    valid ? "Valid" : "Syntax Issue",
                    valid ? "Well-formed display equation" : "Check curly braces nesting in LaTeX formula"));
            }

            processedLine = Regex.Replace(processedLine, @"\\\[[\s\S]*?\\\]", m => new string(' ', m.Length));

            var displayMathMatches = Regex.Matches(processedLine, @"\$\$(?<content>[^\$\n]+)\$\$");
            foreach (Match m in displayMathMatches)
            {
                displayCount++;
                string content = m.Groups["content"].Value;
                int braces = CountUnbalancedBraces(content);
                bool valid = braces == 0;
                if (valid) validCount++;
                else totalUnbalancedBraces += Math.Abs(braces);

                items.Add(new MarkdownMathDelimiterItem(
                    i + 1, m.Index + 1, "Display Math ($$...$$)", content.Trim(),
                    true, braces == 0, false, false, Math.Abs(braces),
                    valid ? "Valid" : "Syntax Issue",
                    valid ? "Well-formed display formula" : "Check curly braces nesting in math formula"));
            }

            processedLine = Regex.Replace(processedLine, @"\$\$[^\$\n]+\$\$", m => new string(' ', m.Length));

            var inlineMathMatches = Regex.Matches(processedLine, @"(?<!\\)\$(?<content>[^\$\n]+)(?<!\\)\$");
            foreach (Match m in inlineMathMatches)
            {
                string content = m.Groups["content"].Value;
                bool hasLeadingTrailingSpace = content.StartsWith(" ") || content.EndsWith(" ");

                if (Regex.IsMatch(content, @"^\d+(\.\d+)?\s*(to|-)\s*\d+") || Regex.IsMatch(content, @"^\d+(\.\d+)?$"))
                {
                    unescapedCurrencyCount++;
                    items.Add(new MarkdownMathDelimiterItem(
                        i + 1, m.Index + 1, "Ambiguous Currency ($...$)", content,
                        true, true, true, hasLeadingTrailingSpace, 0,
                        "Ambiguous Currency",
                        "Escape dollar signs with \\$ for monetary values to prevent math parsing"));
                    continue;
                }

                inlineCount++;
                int braces = CountUnbalancedBraces(content);
                bool valid = braces == 0 && !hasLeadingTrailingSpace;
                if (valid) validCount++;
                else if (braces != 0) totalUnbalancedBraces += Math.Abs(braces);

                string status = valid ? "Valid" : (hasLeadingTrailingSpace ? "Whitespace Issue" : "Syntax Issue");
                string recommendation = valid
                    ? "Well-formed inline math"
                    : (hasLeadingTrailingSpace
                        ? "Remove leading/trailing space immediately inside $ delimiters for standard KaTeX rendering"
                        : "Check curly braces nesting in inline formula");

                items.Add(new MarkdownMathDelimiterItem(
                    i + 1, m.Index + 1, "Inline Math ($...$)", content.Trim(),
                    true, braces == 0, false, hasLeadingTrailingSpace, Math.Abs(braces),
                    status, recommendation));
            }

            processedLine = Regex.Replace(processedLine, @"(?<!\\)\$[^\$\n]+(?<!\\)\$", m => new string(' ', m.Length));

            var remainingDollarMatches = Regex.Matches(processedLine, @"(?<!\\)\$");
            foreach (Match m in remainingDollarMatches)
            {
                string remainder = line.Substring(m.Index);
                if (Regex.IsMatch(remainder, @"^\$\d+([.,]\d+)?(\b|\s)"))
                {
                    unescapedCurrencyCount++;
                    items.Add(new MarkdownMathDelimiterItem(
                        i + 1, m.Index + 1, "Unescaped Currency ($)", remainder.Split(new[] { ' ', '\t' })[0],
                        false, true, true, false, 0,
                        "Unescaped Currency",
                        "Escape currency dollar sign with \\$ to prevent accidental math delimiter parsing"));
                }
                else
                {
                    unclosedCount++;
                    items.Add(new MarkdownMathDelimiterItem(
                        i + 1, m.Index + 1, "Unclosed Math Delimiter ($)", "$",
                        false, false, false, false, 0,
                        "Unclosed Delimiter",
                        "Close matching $ delimiter or escape with \\$"));
                }
            }
        }

        int totalMath = inlineCount + displayCount;
        double score = 100.0;
        if (totalMath == 0 && unclosedCount == 0 && unescapedCurrencyCount == 0)
        {
            score = 100.0;
        }
        else
        {
            score -= (unclosedCount * 15.0);
            score -= (totalUnbalancedBraces * 10.0);
            score -= (unescapedCurrencyCount * 5.0);
            int whitespaceErrors = items.Count(it => it.HasLeadingTrailingSpace);
            score -= (whitespaceErrors * 5.0);
            score = Math.Clamp(score, 0.0, 100.0);
        }
        score = Math.Round(score, 1);

        string category = score switch
        {
            100.0 when totalMath == 0 => "No Math Detected",
            >= 95.0 => "Optimal Math Fidelity",
            >= 80.0 => "High Math Fidelity",
            >= 60.0 => "Moderate Math Fidelity",
            >= 40.0 => "Needs Review",
            _ => "Severe Math Delimiter Issues"
        };

        string summary = FormatMathDelimiterFidelitySummary(
            totalMath, inlineCount, displayCount, validCount,
            unclosedCount, unescapedCurrencyCount, totalUnbalancedBraces,
            score, category);

        return new MarkdownMathDelimiterAudit(
            totalMath, inlineCount, displayCount, validCount,
            unclosedCount, unescapedCurrencyCount, totalUnbalancedBraces,
            score, items, category, summary);
    }

    private static int CountUnbalancedBraces(string text)
    {
        int depth = 0;
        bool inEscape = false;
        foreach (char c in text)
        {
            if (inEscape)
            {
                inEscape = false;
                continue;
            }
            if (c == '\\')
            {
                inEscape = true;
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}') depth--;
        }
        return depth;
    }

    public static string FormatMathDelimiterFidelitySummary(string? markdownText)
        => AuditMarkdownMathDelimiters(markdownText).Summary;

    public static string FormatMathDelimiterFidelitySummary(
        int totalMath, int inlineCount, int displayCount, int validCount,
        int unclosedCount, int currencyCount, int unbalancedBraces,
        double score, string category)
    {
        if (totalMath == 0 && unclosedCount == 0 && currencyCount == 0)
            return "Markdown math delimiter audit: Clean document · 0 math expressions detected";

        string mathSuffix = totalMath == 1 ? string.Empty : "s";
        string braceSuffix = unbalancedBraces == 1 ? string.Empty : "s";

        var parts = new List<string>
        {
            $"{totalMath} math formula{mathSuffix} ({inlineCount} inline, {displayCount} display)",
            $"{validCount} valid"
        };
        if (unclosedCount > 0) parts.Add($"⚠️ {unclosedCount} unclosed");
        if (currencyCount > 0) parts.Add($"{currencyCount} unescaped currency");
        if (unbalancedBraces > 0) parts.Add($"{unbalancedBraces} unbalanced brace{braceSuffix}");

        string details = string.Join(" · ", parts);
        return $"Markdown math delimiter audit ({details} · {category} · {score:0.#}% fidelity score)";
    }

    /// <summary>
    /// Audits markdown text for GFM strikethrough (~~deleted~~), pseudo-HTML underline/del tags (&lt;u&gt;, &lt;ins&gt;, &lt;del&gt;, &lt;s&gt;),
    /// unclosed or dangling tildes, whitespace padding violations (e.g. ~~ text ~~), and typographic styling consistency.
    /// </summary>
    public static MarkdownStrikethroughAudit AuditMarkdownStrikethroughAndUnderline(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown strikethrough & underline audit: Clean document · 0 strikethrough or underline spans detected";
            return new MarkdownStrikethroughAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownStrikethroughItem>(),
                "Clean Baseline",
                emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var items = new List<MarkdownStrikethroughItem>();
        bool inFencedBlock = false;
        string? activeFence = null;

        int gfmCount = 0;
        int pseudoHtmlCount = 0;
        int validCount = 0;
        int unclosedCount = 0;
        int invalidWhitespaceCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                string fenceMarker = trimmed.Substring(0, 3);
                if (!inFencedBlock)
                {
                    inFencedBlock = true;
                    activeFence = fenceMarker;
                    continue;
                }
                else if (activeFence == fenceMarker)
                {
                    inFencedBlock = false;
                    activeFence = null;
                    continue;
                }
            }

            if (inFencedBlock) continue;

            // Mask out inline code backticks: `code`
            var processedLine = Regex.Replace(line, @"`+[^`\n]+`+", m => new string(' ', m.Length));

            // 1. Detect GFM Strikethrough ~~...~~
            var gfmMatches = Regex.Matches(processedLine, @"(?<!~)(~~)(?!~)(.*?)(?<!~)(~~)(?!~)");
            var coveredSpans = new List<(int Start, int End)>();

            foreach (Match m in gfmMatches)
            {
                gfmCount++;
                string inner = m.Groups[2].Value;
                bool hasLeadingSpace = inner.StartsWith(" ") || inner.StartsWith("\t");
                bool hasTrailingSpace = inner.EndsWith(" ") || inner.EndsWith("\t");
                bool isWhitespaceViolation = hasLeadingSpace || hasTrailingSpace;

                if (isWhitespaceViolation)
                {
                    invalidWhitespaceCount++;
                    items.Add(new MarkdownStrikethroughItem(
                        i + 1, m.Index + 1, "GFM Strikethrough", m.Value,
                        true, true, false,
                        "Invalid Delimiter Whitespace",
                        "Remove leading or trailing space inside '~~' delimiters (GFM requires '~~text~~' without inner space)"));
                }
                else
                {
                    validCount++;
                    items.Add(new MarkdownStrikethroughItem(
                        i + 1, m.Index + 1, "GFM Strikethrough", m.Value,
                        true, false, false,
                        "Valid GFM Strikethrough",
                        "Standard GFM strikethrough notation"));
                }

                coveredSpans.Add((m.Index, m.Index + m.Length));
            }

            // Check for unclosed / dangling ~~ on this line
            int idx = 0;
            while (idx < processedLine.Length)
            {
                int tildeIdx = processedLine.IndexOf("~~", idx, StringComparison.Ordinal);
                if (tildeIdx < 0) break;

                bool insideMatched = coveredSpans.Any(s => tildeIdx >= s.Start && tildeIdx < s.End);
                if (!insideMatched)
                {
                    if (tildeIdx > 0 && processedLine[tildeIdx - 1] == '\\')
                    {
                        idx = tildeIdx + 2;
                        continue;
                    }
                    unclosedCount++;
                    items.Add(new MarkdownStrikethroughItem(
                        i + 1, tildeIdx + 1, "Broken Delimiter", "~~",
                        false, false, false,
                        "Unclosed Strikethrough Delimiter",
                        "Ensure '~~' strikethrough markers are properly opened and closed in pairs"));
                    idx = tildeIdx + 2;
                }
                else
                {
                    idx = tildeIdx + 2;
                }
            }

            // 2. Detect Pseudo-HTML tags: <u>, <ins>, <del>, <s>
            var htmlMatches = Regex.Matches(processedLine, @"<(/)?(u|ins|del|s)\b[^>]*>", RegexOptions.IgnoreCase);
            var tagStack = new Stack<(string Tag, int Col)>();

            foreach (Match m in htmlMatches)
            {
                pseudoHtmlCount++;
                string full = m.Value;
                bool isClosing = full.StartsWith("</", StringComparison.Ordinal);
                string tag = m.Groups[2].Value.ToLowerInvariant();

                if (!isClosing)
                {
                    tagStack.Push((tag, m.Index + 1));
                }
                else
                {
                    if (tagStack.Count > 0 && tagStack.Peek().Tag == tag)
                    {
                        var opened = tagStack.Pop();
                        validCount++;
                        items.Add(new MarkdownStrikethroughItem(
                            i + 1, opened.Col, $"HTML <{tag}>", $"<{tag}>...</{tag}>",
                            true, false, true,
                            $"Valid HTML <{tag}> Tag",
                            tag == "u" || tag == "ins"
                                ? "Underline tag detected (compatible with preview & OpenXML w:u)"
                                : "HTML strikethrough tag detected; prefer GFM '~~text~~' for maximum portability"));
                    }
                    else
                    {
                        unclosedCount++;
                        items.Add(new MarkdownStrikethroughItem(
                            i + 1, m.Index + 1, $"HTML </{tag}>", full,
                            false, false, true,
                            $"Orphan Closing </{tag}> Tag",
                            $"Remove unmatched closing tag </{tag}> or provide matching opening <{tag}>"));
                    }
                }
            }

            while (tagStack.Count > 0)
            {
                var unclosed = tagStack.Pop();
                unclosedCount++;
                items.Add(new MarkdownStrikethroughItem(
                    i + 1, unclosed.Col, $"HTML <{unclosed.Tag}>", $"<{unclosed.Tag}>",
                    false, false, true,
                    $"Unclosed <{unclosed.Tag}> Tag",
                    $"Add closing tag </{unclosed.Tag}> to terminate styling span"));
            }
        }

        int totalSpans = gfmCount + pseudoHtmlCount;
        double score = 100.0;
        score -= (unclosedCount * 15.0);
        score -= (invalidWhitespaceCount * 10.0);
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 => (totalSpans == 0 ? "Clean Baseline" : "Consistent Strikethrough & Typography"),
            >= 75.0 => "Minor Delimiter Inconsistencies",
            >= 50.0 => "Malformed Strikethrough Markers",
            _ => "Severe Delimiter Malformations"
        };

        string summary = FormatStrikethroughSummary(
            totalSpans, gfmCount, pseudoHtmlCount, validCount, unclosedCount,
            invalidWhitespaceCount, score, category);

        return new MarkdownStrikethroughAudit(
            totalSpans, gfmCount, pseudoHtmlCount, validCount, unclosedCount,
            invalidWhitespaceCount, score, items, category, summary);
    }

    public static string FormatStrikethroughSummary(string? markdownText)
        => AuditMarkdownStrikethroughAndUnderline(markdownText).Summary;

    public static string FormatStrikethroughSummary(
        int totalSpans, int gfmCount, int pseudoHtmlCount, int validCount,
        int unclosedCount, int invalidWhitespaceCount, double score, string category)
    {
        if (totalSpans == 0 && unclosedCount == 0 && invalidWhitespaceCount == 0)
            return "Markdown strikethrough & underline audit: Clean document · 0 strikethrough or underline spans detected";

        var parts = new List<string>();
        if (gfmCount > 0) parts.Add($"{gfmCount} GFM ~~strike~~");
        if (pseudoHtmlCount > 0) parts.Add($"ℹ {pseudoHtmlCount} HTML tags (<del>/<u>)");
        if (unclosedCount > 0) parts.Add($"⚠ {unclosedCount} unclosed");
        if (invalidWhitespaceCount > 0) parts.Add($"⚠ {invalidWhitespaceCount} invalid whitespace padding");
        if (validCount > 0) parts.Add($"✓ {validCount} valid");

        string details = string.Join(" · ", parts);
        return $"Markdown strikethrough & underline audit ({details} · {category} · {score:0.#}% consistency score)";
    }
    /// <summary>
    /// Audits markdown task lists and checkbox syntax completeness:
    /// scans for standard GFM task lists (- [ ], - [x], * [ ], + [ ]),
    /// malformed brackets (- [], - [  ], - [v]), orphaned checkboxes without list bullets,
    /// empty task descriptions, and computes task completion metrics and compliance score.
    /// </summary>
    public static MarkdownTaskListsAudit AuditMarkdownTaskLists(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown task lists audit: Clean document · 0 task list items detected";
            return new MarkdownTaskListsAudit(
                0, 0, 0, 0, 0, 0.0, 100.0,
                Array.Empty<MarkdownTaskListItem>(),
                "Clean Baseline",
                emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var items = new List<MarkdownTaskListItem>();
        bool inFencedBlock = false;
        string? activeFence = null;

        int totalTasks = 0;
        int completedCount = 0;
        int pendingCount = 0;
        int malformedCount = 0;
        int orphanedCount = 0;
        int emptyDescCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Track code fence boundaries
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                string fenceMarker = trimmed.Substring(0, 3);
                if (!inFencedBlock)
                {
                    inFencedBlock = true;
                    activeFence = fenceMarker;
                }
                else if (activeFence == fenceMarker)
                {
                    inFencedBlock = false;
                    activeFence = null;
                }
                continue;
            }

            if (inFencedBlock || string.IsNullOrWhiteSpace(trimmed))
                continue;

            // 1. Valid GFM Task List item: e.g. "- [ ] Task" or "- [x] Done" or "* [X] Finished" or "1. [ ] Ordered"
            var validMatch = Regex.Match(line, @"^(\s*)([-*+]|\d+\.)\s+\[([ xX])\](\s*.*)$");
            if (validMatch.Success)
            {
                totalTasks++;
                char checkChar = validMatch.Groups[3].Value[0];
                bool isCompleted = checkChar == 'x' || checkChar == 'X';
                string taskText = validMatch.Groups[4].Value.Trim();

                if (isCompleted)
                    completedCount++;
                else
                    pendingCount++;

                if (string.IsNullOrWhiteSpace(taskText))
                {
                    emptyDescCount++;
                    items.Add(new MarkdownTaskListItem(
                        i + 1, line, "(empty description)",
                        isCompleted ? "Completed" : "Pending",
                        "Warning",
                        "Task item has no descriptive text",
                        "Add a concise actionable task description after the checkbox"));
                }
                else
                {
                    items.Add(new MarkdownTaskListItem(
                        i + 1, line, taskText,
                        isCompleted ? "Completed" : "Pending",
                        "Info",
                        "Valid GFM task checkbox",
                        "Syntax is complete"));
                }
                continue;
            }

            // 2. Malformed Checkbox: e.g. "- []" (missing space) or "- [  ]" (multiple spaces) or "- [v]" / "- [-]" (non-standard)
            var malformedMatch = Regex.Match(line, @"^(\s*)([-*+]|\d+\.)\s*\[([^\]]*)\](\s*.*)$");
            if (malformedMatch.Success)
            {
                string inner = malformedMatch.Groups[3].Value;
                string rest = malformedMatch.Groups[4].Value.Trim();
                totalTasks++;
                malformedCount++;

                string issue = inner.Length == 0
                    ? "Checkbox brackets are empty (- []) without whitespace separator"
                    : inner.Length > 1 && inner.Trim().Length == 0
                        ? "Checkbox brackets contain redundant whitespace (- [  ])"
                        : $"Checkbox contains non-standard status mark '[- {inner}]'";

                items.Add(new MarkdownTaskListItem(
                    i + 1, line, rest,
                    "Malformed",
                    "Error",
                    issue,
                    "Use '- [ ]' for pending tasks or '- [x]' for completed tasks"));
                continue;
            }

            // 3. Orphaned Checkbox: e.g. "[ ] Task" or "[x] Done" without list bullet
            var orphanedMatch = Regex.Match(line, @"^(\s*)\[([ xX])\](\s+.*)$");
            if (orphanedMatch.Success)
            {
                totalTasks++;
                orphanedCount++;
                string taskText = orphanedMatch.Groups[3].Value.Trim();

                items.Add(new MarkdownTaskListItem(
                    i + 1, line, taskText,
                    "Orphaned",
                    "Warning",
                    "Checkbox lacks list bullet prefix (missing '-', '*', '+' or '1.')",
                    "Prepend '- ' before the checkbox for valid GFM markdown parsing"));
                continue;
            }
        }

        double completionPercent = totalTasks > 0
            ? Math.Round(((double)completedCount / totalTasks) * 100.0, 1)
            : 0.0;

        double complianceScore = 100.0;
        complianceScore -= (malformedCount * 18.0);
        complianceScore -= (orphanedCount * 12.0);
        complianceScore -= (emptyDescCount * 5.0);
        complianceScore = Math.Round(Math.Clamp(complianceScore, 0.0, 100.0), 1);

        string category = complianceScore switch
        {
            >= 90.0 => "Optimal Task List Syntax",
            >= 75.0 => "Good Task List Hygiene",
            >= 60.0 => "Moderate Checklist Ambiguities",
            >= 40.0 => "High Checklist Formatting Errors",
            _ => "Severe Task List Syntax Malformations"
        };

        string summary = FormatTaskListsSummary(
            totalTasks, completedCount, pendingCount, malformedCount, orphanedCount,
            completionPercent, complianceScore, category);

        return new MarkdownTaskListsAudit(
            totalTasks, completedCount, pendingCount, malformedCount, orphanedCount,
            completionPercent, complianceScore, items, category, summary);
    }

    public static string FormatTaskListsSummary(string? markdownText)
        => AuditMarkdownTaskLists(markdownText).Summary;

    public static string FormatTaskListsSummary(
        int totalTasks, int completedCount, int pendingCount, int malformedCount,
        int orphanedCount, double completionPercent, double score, string category)
    {
        if (totalTasks == 0 && malformedCount == 0 && orphanedCount == 0)
            return "Markdown task lists audit: Clean document · 0 task list items detected";

        var parts = new List<string>();
        parts.Add($"{totalTasks} tasks: {completedCount} completed [{completionPercent:0.#}%], {pendingCount} pending");
        if (malformedCount > 0) parts.Add($"⚠ {malformedCount} malformed");
        if (orphanedCount > 0) parts.Add($"⚠ {orphanedCount} orphaned");

        string details = string.Join(" · ", parts);
        return $"Markdown task lists audit ({details} · {category} · {score:0.#}% compliance score)";
    }


    /// <summary>
    /// Audits markdown footnote references and definitions:
    /// scans document markdown for valid footnote references ([^1], [^note]),
    /// footnote definitions ([^1]: text), unreferenced orphan definitions,
    /// missing definitions (references without definitions), duplicate definition labels,
    /// and scores footnote structural integrity (0-100%).
    /// </summary>
    public static MarkdownFootnoteIntegrityAudit AuditMarkdownFootnotes(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown footnotes audit: Clean document · 0 footnote references detected";
            return new MarkdownFootnoteIntegrityAudit(
                0, 0, 0, 0, 0, 0, true, 100.0,
                Array.Empty<MarkdownFootnoteIntegrityItem>(),
                "Clean Baseline",
                emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var items = new List<MarkdownFootnoteIntegrityItem>();
        bool inFencedBlock = false;
        string? activeFence = null;

        var defMap = new Dictionary<string, (int LineNumber, string Excerpt)>(StringComparer.OrdinalIgnoreCase);
        var defDuplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defDuplicateItems = new List<(string Label, int LineNumber)>();
        var refs = new List<(string Label, int LineNumber, string LineText)>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Track fenced code blocks
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                string fenceMarker = trimmed.Substring(0, 3);
                if (!inFencedBlock)
                {
                    inFencedBlock = true;
                    activeFence = fenceMarker;
                }
                else if (activeFence == fenceMarker)
                {
                    inFencedBlock = false;
                    activeFence = null;
                }
                continue;
            }

            if (inFencedBlock || string.IsNullOrWhiteSpace(trimmed))
                continue;

            // 1. Footnote definition: e.g. [^1]: This is footnote text
            var defMatch = Regex.Match(line, @"^\s*\[\^([^\]]+)\]:\s*(.*)$");
            if (defMatch.Success)
            {
                string label = defMatch.Groups[1].Value.Trim();
                string excerpt = defMatch.Groups[2].Value.Trim();
                if (defMap.ContainsKey(label))
                {
                    defDuplicates.Add(label);
                    defDuplicateItems.Add((label, i + 1));
                }
                else
                {
                    defMap[label] = (i + 1, excerpt);
                }
                continue;
            }

            // 2. Footnote references in line: [^label]
            var refMatches = Regex.Matches(line, @"\[\^([^\]]+)\](?!:)");
            foreach (Match m in refMatches)
            {
                string label = m.Groups[1].Value.Trim();
                refs.Add((label, i + 1, line));
            }
        }

        int totalDefs = defMap.Count + defDuplicateItems.Count;
        int totalRefs = refs.Count;

        var referencedLabels = new HashSet<string>(refs.Select(r => r.Label), StringComparer.OrdinalIgnoreCase);
        var definedLabels = new HashSet<string>(defMap.Keys, StringComparer.OrdinalIgnoreCase);

        var missingDefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in refs)
        {
            if (!definedLabels.Contains(r.Label))
            {
                missingDefs.Add(r.Label);
                items.Add(new MarkdownFootnoteIntegrityItem(
                    r.LineNumber, r.Label, "Reference",
                    IsResolved: false,
                    IsMissingDefinition: true,
                    IsUnreferencedOrphan: false,
                    IsDuplicate: false,
                    r.LineText,
                    $"Add definition '[^{r.Label}]: ...' to resolve reference"));
            }
            else
            {
                items.Add(new MarkdownFootnoteIntegrityItem(
                    r.LineNumber, r.Label, "Reference",
                    IsResolved: true,
                    IsMissingDefinition: false,
                    IsUnreferencedOrphan: false,
                    IsDuplicate: false,
                    r.LineText,
                    "Reference correctly resolves to definition"));
            }
        }

        var orphanDefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in defMap)
        {
            if (!referencedLabels.Contains(kvp.Key))
            {
                orphanDefs.Add(kvp.Key);
                items.Add(new MarkdownFootnoteIntegrityItem(
                    kvp.Value.LineNumber, kvp.Key, "Definition",
                    IsResolved: false,
                    IsMissingDefinition: false,
                    IsUnreferencedOrphan: true,
                    IsDuplicate: defDuplicates.Contains(kvp.Key),
                    kvp.Value.Excerpt,
                    $"Footnote '[^{kvp.Key}]' is defined but never referenced in document body"));
            }
            else
            {
                items.Add(new MarkdownFootnoteIntegrityItem(
                    kvp.Value.LineNumber, kvp.Key, "Definition",
                    IsResolved: true,
                    IsMissingDefinition: false,
                    IsUnreferencedOrphan: false,
                    IsDuplicate: defDuplicates.Contains(kvp.Key),
                    kvp.Value.Excerpt,
                    "Definition is referenced in document body"));
            }
        }

        foreach (var dup in defDuplicateItems)
        {
            items.Add(new MarkdownFootnoteIntegrityItem(
                dup.LineNumber, dup.Label, "Definition",
                IsResolved: false,
                IsMissingDefinition: false,
                IsUnreferencedOrphan: false,
                IsDuplicate: true,
                $"Duplicate definition of '[^{dup.Label}]'",
                $"Remove or rename duplicate footnote definition on line {dup.LineNumber}"));
        }

        // Sequential numeric check
        bool isSequentialNumeric = true;
        var numericRefs = refs
            .Select(r => int.TryParse(r.Label, out int n) ? (int?)n : null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .ToList();

        if (numericRefs.Count > 1)
        {
            int expected = 1;
            foreach (var num in numericRefs)
            {
                if (num != expected && num != expected - 1)
                {
                    isSequentialNumeric = false;
                    break;
                }
                if (num == expected) expected++;
            }
        }

        int resolvedCount = refs.Count(r => definedLabels.Contains(r.Label));
        int missingCount = missingDefs.Count;
        int orphanCount = orphanDefs.Count;
        int duplicateCount = defDuplicates.Count + defDuplicateItems.Count;

        double integrityScore = 100.0;
        integrityScore -= (missingCount * 25.0);
        integrityScore -= (orphanCount * 12.0);
        integrityScore -= (duplicateCount * 15.0);
        if (!isSequentialNumeric && numericRefs.Count > 0) integrityScore -= 5.0;
        integrityScore = Math.Round(Math.Clamp(integrityScore, 0.0, 100.0), 1);

        string category = integrityScore switch
        {
            >= 95.0 => "Pristine Footnote Integrity",
            >= 80.0 => "Good Footnote Consistency",
            >= 60.0 => "Moderate Footnote Drift",
            >= 40.0 => "High Missing Definition Errors",
            _ => "Broken Footnote Reference Structure"
        };

        string summary = FormatFootnotesSummary(
            totalDefs, totalRefs, resolvedCount, missingCount, orphanCount, duplicateCount,
            isSequentialNumeric, integrityScore, category);

        return new MarkdownFootnoteIntegrityAudit(
            totalDefs, totalRefs, resolvedCount, missingCount, orphanCount, duplicateCount,
            isSequentialNumeric, integrityScore, items, category, summary);
    }

    public static string FormatFootnotesSummary(string? markdownText)
        => AuditMarkdownFootnotes(markdownText).Summary;

    public static string FormatFootnotesSummary(
        int totalDefs, int totalRefs, int resolvedCount, int missingCount,
        int orphanCount, int duplicateCount, bool isSequential, double score, string category)
    {
        if (totalDefs == 0 && totalRefs == 0)
            return "Markdown footnotes audit: Clean document · 0 footnote references detected";

        var parts = new List<string>();
        parts.Add($"{totalRefs} refs, {totalDefs} defs ({resolvedCount} resolved)");
        if (missingCount > 0) parts.Add($"⚠ {missingCount} missing defs");
        if (orphanCount > 0) parts.Add($"ℹ {orphanCount} unreferenced");
        if (duplicateCount > 0) parts.Add($"🚨 {duplicateCount} duplicates");
        if (!isSequential) parts.Add("non-sequential");

        string details = string.Join(" · ", parts);
        return $"Markdown footnotes audit ({details} · {category} · {score:0.#}% integrity score)";
    }

    /// <summary>
    /// Audits markdown blockquote nesting levels, ragged/broken prefixes, standard blockquotes vs GFM alert callouts,
    /// nested callout structures, and scores quotation structural hierarchy (0-100%).
    /// </summary>
    public static MarkdownBlockquoteNestingAudit AuditMarkdownBlockquoteNesting(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = "Markdown blockquote audit: Clean document · 0 quoted lines detected";
            return new MarkdownBlockquoteNestingAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownBlockquoteNestingItem>(),
                "Clean Baseline",
                emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Split('\n');
        var items = new List<MarkdownBlockquoteNestingItem>();
        bool inFencedBlock = false;
        string? activeFence = null;

        int totalQuotedLines = 0;
        int maxNestingDepth = 0;
        int standardQuoteCount = 0;
        int alertCalloutCount = 0;
        int nestedCalloutCount = 0;
        int raggedPrefixCount = 0;

        int prevDepth = 0;
        bool prevWasQuoted = false;

        var alertRegex = new Regex(@"^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]", RegexOptions.IgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string rawLine = lines[i];
            string trimmedStart = rawLine.TrimStart();

            // Handle fenced code blocks inside document
            if (trimmedStart.StartsWith("```") || trimmedStart.StartsWith("~~~"))
            {
                string fenceMarker = trimmedStart.Substring(0, 3);
                if (!inFencedBlock)
                {
                    inFencedBlock = true;
                    activeFence = fenceMarker;
                }
                else if (trimmedStart.StartsWith(activeFence ?? "```"))
                {
                    inFencedBlock = false;
                    activeFence = null;
                }
                prevDepth = 0;
                prevWasQuoted = false;
                continue;
            }

            if (inFencedBlock)
            {
                continue;
            }

            // Count leading blockquote depth '>'
            int depth = 0;
            int idx = 0;
            while (idx < rawLine.Length)
            {
                char c = rawLine[idx];
                if (c == '>')
                {
                    depth++;
                    idx++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    idx++;
                }
                else
                {
                    break;
                }
            }

            if (depth == 0)
            {
                // If previous line was quoted and current line is non-empty plain text without an empty blank line,
                // this can be a ragged/lazy quote continuation
                if (prevWasQuoted && !string.IsNullOrWhiteSpace(rawLine) && !rawLine.StartsWith('#'))
                {
                    raggedPrefixCount++;
                    items.Add(new MarkdownBlockquoteNestingItem(
                        lineNum, 0, string.Empty, false, null, true, false,
                        rawLine.Trim(),
                        "Add matching '>' blockquote prefix to continue blockquote paragraph or insert a blank line to close it"));
                }
                prevDepth = 0;
                prevWasQuoted = false;
                continue;
            }

            totalQuotedLines++;
            if (depth > maxNestingDepth) maxNestingDepth = depth;

            string contentAfterQuotes = rawLine.Substring(idx).Trim();
            string prefix = rawLine.Substring(0, idx).Trim();

            // Check if jumping depth by > 1 from previous quoted line
            bool isRagged = false;
            if (prevWasQuoted && depth > prevDepth + 1)
            {
                isRagged = true;
                raggedPrefixCount++;
            }

            // Check if this line introduces a GFM alert callout
            var alertMatch = alertRegex.Match(contentAfterQuotes);
            bool isAlert = alertMatch.Success;
            string? alertType = isAlert ? alertMatch.Groups[1].Value.ToUpperInvariant() : null;
            bool isNestedCallout = isAlert && depth > 1;

            if (isAlert)
            {
                alertCalloutCount++;
                if (isNestedCallout)
                {
                    nestedCalloutCount++;
                }
            }
            else
            {
                standardQuoteCount++;
            }

            string rec = "Valid blockquote syntax";
            if (isRagged)
            {
                rec = $"Gradually increase blockquote nesting depth by 1 level instead of jumping from level {prevDepth} to {depth}";
            }
            else if (isNestedCallout)
            {
                rec = "Avoid deeply nesting alert callouts inside multi-level quotes; place top-level for maximum visibility";
            }
            else if (depth > 4)
            {
                rec = $"Blockquote nesting depth of {depth} is very deep; consider flattening quotation hierarchy";
            }

            items.Add(new MarkdownBlockquoteNestingItem(
                lineNum, depth, prefix, isAlert, alertType, isRagged, isNestedCallout,
                contentAfterQuotes.Length > 60 ? contentAfterQuotes.Substring(0, 57) + "..." : contentAfterQuotes,
                rec));

            prevDepth = depth;
            prevWasQuoted = true;
        }

        double score = 100.0;
        score -= (raggedPrefixCount * 12.0);
        score -= (nestedCalloutCount * 8.0);
        if (maxNestingDepth > 4)
        {
            score -= ((maxNestingDepth - 4) * 5.0);
        }
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 => "Pristine Hierarchy",
            >= 85.0 => "High Quality",
            >= 70.0 => "Moderate Quality",
            >= 50.0 => "Ragged Structure",
            _ => "Deeply Tangled"
        };

        string summary = FormatBlockquoteNestingHierarchySummary(
            totalQuotedLines, maxNestingDepth, standardQuoteCount, alertCalloutCount,
            nestedCalloutCount, raggedPrefixCount, score, category);

        return new MarkdownBlockquoteNestingAudit(
            totalQuotedLines, maxNestingDepth, standardQuoteCount, alertCalloutCount,
            nestedCalloutCount, raggedPrefixCount, score, items, category, summary);
    }

    public static string FormatBlockquoteNestingHierarchySummary(string? markdownText)
        => AuditMarkdownBlockquoteNesting(markdownText).Summary;

    public static string FormatBlockquoteNestingHierarchySummary(
        int totalLines, int maxDepth, int standardCount, int alertCount,
        int nestedCalloutCount, int raggedCount, double score, string category)
    {
        if (totalLines == 0 && raggedCount == 0)
            return "Markdown blockquote audit: Clean document · 0 quoted lines detected";

        var parts = new List<string>();
        parts.Add($"{totalLines} quoted lines (max depth {maxDepth})");
        if (alertCount > 0) parts.Add($"{alertCount} alert callouts");
        if (nestedCalloutCount > 0) parts.Add($"ℹ {nestedCalloutCount} nested alerts");
        if (raggedCount > 0) parts.Add($"⚠ {raggedCount} ragged prefixes");

        string details = string.Join(" · ", parts);
        return $"Markdown blockquote audit ({details} · {category} · {score:0.#}% hierarchy score)";
    }


    public static MarkdownHorizontalRuleAudit AuditMarkdownHorizontalRules(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatHorizontalRulesSummary(0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownHorizontalRuleAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownHorizontalRuleItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownHorizontalRuleItem>();

        int totalBreaks = 0;
        int dashBreaks = 0;
        int asteriskBreaks = 0;
        int underscoreBreaks = 0;
        int missingBlankLines = 0;

        // Track YAML frontmatter at document start
        int frontmatterEndLine = -1;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    frontmatterEndLine = i;
                    break;
                }
            }
        }

        // Horizontal rule regex: 3 or more *, -, or _ with optional spaces
        var hrRegex = new Regex(@"^[ \t]*(?<marker>[*_\-])([ \t]*\k<marker>){2,}[ \t]*$");

        for (int i = 0; i < lines.Length; i++)
        {
            // Skip frontmatter lines
            if (i <= frontmatterEndLine)
            {
                continue;
            }

            string rawLine = lines[i];
            string trimmed = rawLine.Trim();

            // Must not be a table row or contain pipes
            if (trimmed.Contains('|'))
            {
                continue;
            }

            var match = hrRegex.Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            char markerChar = match.Groups["marker"].Value[0];

            // In CommonMark, a line of '-' following a non-empty text line is a Setext H2 heading, NOT an HR.
            bool hasPrecedingBlankLine = (i == 0 || string.IsNullOrWhiteSpace(lines[i - 1]));
            bool isSetextAmbiguity = false;
            if (!hasPrecedingBlankLine && markerChar == '-')
            {
                isSetextAmbiguity = true;
                continue;
            }

            // Check following line
            bool hasFollowingBlankLine = (i == lines.Length - 1 || string.IsNullOrWhiteSpace(lines[i + 1]));

            totalBreaks++;
            string canonicalType = markerChar switch
            {
                '-' => "Hyphen/Dash (---)",
                '*' => "Asterisk (***)",
                '_' => "Underscore (___)",
                _ => "Other"
            };

            if (markerChar == '-') dashBreaks++;
            else if (markerChar == '*') asteriskBreaks++;
            else if (markerChar == '_') underscoreBreaks++;

            bool missingBlank = !hasPrecedingBlankLine || !hasFollowingBlankLine;
            if (missingBlank)
            {
                missingBlankLines++;
            }

            string recommendation = missingBlank
                ? "Surround thematic break with blank lines on both sides to avoid parser ambiguities and maintain typographic pacing"
                : "Thematic break is cleanly spaced";

            items.Add(new MarkdownHorizontalRuleItem(
                i + 1, trimmed, canonicalType, hasPrecedingBlankLine, hasFollowingBlankLine,
                isSetextAmbiguity, recommendation));
        }

        int distinctStyles = 0;
        if (dashBreaks > 0) distinctStyles++;
        if (asteriskBreaks > 0) distinctStyles++;
        if (underscoreBreaks > 0) distinctStyles++;

        int mixedMarkerStyleCount = distinctStyles > 1 ? distinctStyles : 0;

        double score = 100.0;
        if (totalBreaks > 0)
        {
            score -= (missingBlankLines * 12.0);
            if (mixedMarkerStyleCount > 1)
            {
                score -= ((mixedMarkerStyleCount - 1) * 15.0);
            }
        }
        score = Math.Round(Math.Clamp(score, 0.0, 100.0), 1);

        string category = score switch
        {
            >= 95.0 => "Pristine Spacing",
            >= 80.0 => "Consistent Breaks",
            >= 60.0 => "Moderate Spacing Defects",
            >= 40.0 => "Ragged Whitespace Around Breaks",
            _ => "Disordered Thematic Breaks"
        };

        string summary = FormatHorizontalRulesSummary(
            totalBreaks, dashBreaks, asteriskBreaks, underscoreBreaks,
            missingBlankLines, mixedMarkerStyleCount, score, category);

        return new MarkdownHorizontalRuleAudit(
            totalBreaks, dashBreaks, asteriskBreaks, underscoreBreaks,
            missingBlankLines, mixedMarkerStyleCount, score, items, category, summary);
    }

    public static string FormatHorizontalRulesSummary(string? markdownText)
        => AuditMarkdownHorizontalRules(markdownText).Summary;

    public static string FormatHorizontalRulesSummary(
        int totalBreaks, int dashBreaks, int asteriskBreaks, int underscoreBreaks,
        int missingBlankLines, int mixedMarkerStyles, double score, string category)
    {
        if (totalBreaks == 0)
            return "Markdown thematic break audit: Clean document · 0 horizontal rules detected";

        var parts = new List<string>();
        parts.Add($"{totalBreaks} thematic breaks");

        var styles = new List<string>();
        if (dashBreaks > 0) styles.Add($"{dashBreaks} '---'");
        if (asteriskBreaks > 0) styles.Add($"{asteriskBreaks} '***'");
        if (underscoreBreaks > 0) styles.Add($"{underscoreBreaks} '___'");
        parts.Add(string.Join("/", styles));

        if (missingBlankLines > 0) parts.Add($"⚠ {missingBlankLines} missing blank lines");
        if (mixedMarkerStyles > 1) parts.Add($"⚠ {mixedMarkerStyles} mixed marker styles");

        string details = string.Join(" · ", parts);
        return $"Markdown thematic break audit ({details} · {category} · {score:0.#}% spacing score)";
    }

    public static MarkdownDefinitionListAudit AuditMarkdownDefinitionLists(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatDefinitionListsSummary(0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownDefinitionListAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownDefinitionListItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownDefinitionListItem>();

        int totalTerms = 0;
        int totalDefinitions = 0;
        int multiLineDefinitions = 0;
        int orphanDefinitions = 0;

        var seenTerms = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int duplicateTermsCount = 0;

        // Track frontmatter
        int frontmatterEndLine = -1;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    frontmatterEndLine = i;
                    break;
                }
            }
        }

        bool insideCodeFence = false;
        string? activeTerm = null;
        int activeTermLine = -1;
        bool precedingLineWasBlank = false;

        for (int i = 0; i < lines.Length; i++)
        {
            if (i <= frontmatterEndLine) continue;

            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                insideCodeFence = !insideCodeFence;
                activeTerm = null;
                continue;
            }

            if (insideCodeFence) continue;

            if (string.IsNullOrWhiteSpace(line))
            {
                precedingLineWasBlank = true;
                continue;
            }

            // Check if this line is a definition marker starting with ':'
            // In Markdown definition lists: ': definition text' optionally preceded by up to 4 spaces or a tab
            bool isDefMarker = false;
            string defText = string.Empty;
            int colonIndex = line.IndexOf(':');

            if (colonIndex >= 0)
            {
                string prefix = line.Substring(0, colonIndex);
                if (prefix.Length <= 4 && prefix.Trim().Length == 0)
                {
                    isDefMarker = true;
                    defText = line.Substring(colonIndex + 1).Trim();
                }
            }

            if (isDefMarker)
            {
                totalDefinitions++;
                string termName;
                bool isOrphan = false;

                if (!string.IsNullOrEmpty(activeTerm))
                {
                    termName = activeTerm;
                }
                else
                {
                    isOrphan = true;
                    orphanDefinitions++;
                    termName = "[Orphan Definition]";
                }

                // Check for multi-line continuation in subsequent lines
                bool isMultiLine = false;
                int lookahead = i + 1;
                while (lookahead < lines.Length)
                {
                    string nextLine = lines[lookahead];
                    if (string.IsNullOrWhiteSpace(nextLine)) break;
                    if (nextLine.TrimStart().StartsWith(":")) break;
                    if (nextLine.StartsWith("  ") || nextLine.StartsWith("\t"))
                    {
                        isMultiLine = true;
                        lookahead++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (isMultiLine)
                {
                    multiLineDefinitions++;
                }

                bool isDuplicate = false;
                if (!isOrphan && seenTerms.TryGetValue(termName, out int count))
                {
                    if (count > 1)
                    {
                        isDuplicate = true;
                    }
                }

                string recommendation = isOrphan
                    ? "Add a preceding term on the line above this definition."
                    : isDuplicate
                        ? "Consolidate duplicate glossary term definitions."
                        : string.IsNullOrEmpty(defText)
                            ? "Provide definition text following the colon marker."
                            : "Clean glossary term-definition pair.";

                items.Add(new MarkdownDefinitionListItem(
                    i + 1,
                    termName,
                    defText,
                    isMultiLine,
                    precedingLineWasBlank,
                    isDuplicate,
                    recommendation));

                precedingLineWasBlank = false;
                continue;
            }

            // If not a definition marker, evaluate if this could be a Term
            if (!line.StartsWith("  ") && !line.StartsWith("\t") && !trimmed.StartsWith("#") && !trimmed.StartsWith(">") && !trimmed.StartsWith("|") && !trimmed.StartsWith("- ") && !trimmed.StartsWith("* "))
            {
                activeTerm = trimmed;
                activeTermLine = i + 1;

                if (!seenTerms.ContainsKey(activeTerm))
                {
                    seenTerms[activeTerm] = 1;
                    totalTerms++;
                }
                else
                {
                    seenTerms[activeTerm]++;
                    duplicateTermsCount++;
                }
            }
            else
            {
                activeTerm = null;
            }

            precedingLineWasBlank = false;
        }

        // Calculate score
        double score = 100.0;
        if (orphanDefinitions > 0) score -= orphanDefinitions * 15.0;
        if (duplicateTermsCount > 0) score -= duplicateTermsCount * 10.0;
        score = Math.Clamp(score, 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 => "Flawless Definition Glossary",
            >= 80.0 => "Consistent Definition Lists",
            >= 60.0 => "Moderate Formatting Discrepancies",
            _ => "Malformed Definition Syntax"
        };

        string summary = FormatDefinitionListsSummary(
            totalTerms, totalDefinitions, multiLineDefinitions,
            duplicateTermsCount, orphanDefinitions, score, category);

        return new MarkdownDefinitionListAudit(
            totalTerms, totalDefinitions, multiLineDefinitions,
            duplicateTermsCount, orphanDefinitions, score, items, category, summary);
    }


    public static MarkdownAbbreviationAudit AuditMarkdownAbbreviations(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatAbbreviationsSummary(0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownAbbreviationAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownAbbreviationItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownAbbreviationItem>();

        var abbrDeclRegex = new Regex(@"^\*\[(?<abbr>[A-Za-z0-9_\-\.]+?)\]:\s*(?<def>.*)$");
        var declaredAbbrs = new Dictionary<string, (string Definition, int LineNumber)>(StringComparer.OrdinalIgnoreCase);
        var conflictingAbbrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int conflictCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var match = abbrDeclRegex.Match(lines[i].Trim());
            if (match.Success)
            {
                string abbr = match.Groups["abbr"].Value.Trim();
                string def = match.Groups["def"].Value.Trim();

                if (declaredAbbrs.TryGetValue(abbr, out var existing))
                {
                    if (!string.Equals(existing.Definition, def, StringComparison.OrdinalIgnoreCase))
                    {
                        conflictingAbbrs.Add(abbr);
                        conflictCount++;
                    }
                }
                else
                {
                    declaredAbbrs[abbr] = (def, i + 1);
                }
            }
        }

        int frontmatterEndLine = -1;
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    frontmatterEndLine = i;
                    break;
                }
            }
        }

        bool insideCodeFence = false;
        var acronymUsages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var acronymFirstLine = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var acronymTokenRegex = new Regex(@"\b[A-Z][A-Z0-9]{1,5}\b");
        var inlineCodeRegex = new Regex(@"`[^`]*`");
        var linkUrlRegex = new Regex(@"\[(?<text>[^\]]*)\]\((?<url>[^)\s]+)\)");
        var rawUrlRegex = new Regex(@"https?://\S+");

        for (int i = 0; i < lines.Length; i++)
        {
            if (i <= frontmatterEndLine) continue;

            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                insideCodeFence = !insideCodeFence;
                continue;
            }

            if (insideCodeFence) continue;
            if (abbrDeclRegex.IsMatch(trimmed)) continue;

            string cleanLine = inlineCodeRegex.Replace(line, " ");
            cleanLine = linkUrlRegex.Replace(cleanLine, "${text}");
            cleanLine = rawUrlRegex.Replace(cleanLine, " ");

            var matches = acronymTokenRegex.Matches(cleanLine);
            foreach (Match m in matches)
            {
                string token = m.Value;
                if (token.Length < 2) continue;

                if (!acronymUsages.ContainsKey(token))
                {
                    acronymUsages[token] = 0;
                    acronymFirstLine[token] = i + 1;
                }
                acronymUsages[token]++;
            }
        }

        int definedCount = 0;
        int undefinedCount = 0;
        int totalUsages = 0;

        foreach (var kvp in declaredAbbrs)
        {
            string abbr = kvp.Key;
            string def = kvp.Value.Definition;
            int lineNum = kvp.Value.LineNumber;
            int usages = acronymUsages.TryGetValue(abbr, out int u) ? u : 0;
            totalUsages += usages;
            definedCount++;

            bool hasConflict = conflictingAbbrs.Contains(abbr);
            string rec = hasConflict
                ? $"Resolve conflicting expansions for abbreviation '{abbr}'."
                : usages == 0
                    ? $"Abbreviation '{abbr}' is defined but never used in document body."
                    : $"Documented abbreviation '{abbr}' with {usages} occurrence{(usages == 1 ? "" : "s")}.";

            items.Add(new MarkdownAbbreviationItem(
                lineNum,
                abbr,
                def,
                usages,
                true,
                hasConflict,
                rec));
        }

        foreach (var kvp in acronymUsages)
        {
            string token = kvp.Key;
            if (declaredAbbrs.ContainsKey(token)) continue;

            int usages = kvp.Value;
            totalUsages += usages;
            undefinedCount++;
            int lineNum = acronymFirstLine[token];

            string rec = usages > 1
                ? $"Acronym '{token}' appears {usages} times; define with *[{token}]: Full Expansion."
                : $"Single-occurrence acronym '{token}'; consider defining with *[{token}]: Full Expansion.";

            items.Add(new MarkdownAbbreviationItem(
                lineNum,
                token,
                null,
                usages,
                false,
                false,
                rec));
        }

        int totalAbbreviations = declaredAbbrs.Count;
        double score = 100.0;
        if (conflictCount > 0) score -= conflictCount * 15.0;
        if (undefinedCount > 0) score -= Math.Min(50.0, undefinedCount * 4.0);
        score = Math.Clamp(score, 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 => "Flawless Abbreviation Governance",
            >= 80.0 => "Consistent Acronym Usage",
            >= 60.0 => "Unreferenced Acronyms",
            _ => "Malformed or Conflicting Abbreviations"
        };

        string summary = FormatAbbreviationsSummary(
            totalAbbreviations, definedCount, undefinedCount, conflictCount, totalUsages, score, category);

        return new MarkdownAbbreviationAudit(
            totalAbbreviations, definedCount, undefinedCount, conflictCount, totalUsages, score, items, category, summary);
    }

    public static string FormatAbbreviationsSummary(string? markdownText)
        => AuditMarkdownAbbreviations(markdownText).Summary;

    public static string FormatAbbreviationsSummary(
        int totalAbbreviations, int definedAcronyms, int undefinedAcronyms, int conflictingDefinitions,
        int totalUsages, double score, string category)
    {
        if (totalAbbreviations == 0 && undefinedAcronyms == 0)
            return "Markdown abbreviation audit: Clean document · 0 acronyms or abbreviation markers detected";

        var parts = new List<string>();
        parts.Add($"{totalAbbreviations} defined abbreviation{(totalAbbreviations == 1 ? "" : "s")}");
        if (undefinedAcronyms > 0) parts.Add($"ℹ {undefinedAcronyms} undefined acronym{(undefinedAcronyms == 1 ? "" : "s")}");
        if (conflictingDefinitions > 0) parts.Add($"⚠ {conflictingDefinitions} conflicting definition{(conflictingDefinitions == 1 ? "" : "s")}");
        parts.Add($"{totalUsages} body usage{(totalUsages == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown abbreviation audit ({details} · {category} · {score:0.#}% governance score)";
    }

    public static MarkdownTableCellAudit AuditMarkdownTableCellWrapping(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatTableCellWrappingSummary(0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownTableCellAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTableCellRowItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownTableCellRowItem>();

        bool insideCodeFence = false;
        int totalTables = 0;
        int totalRows = 0;
        int mismatchedRows = 0;
        int missingDelimiterTables = 0;
        int unbalancedPipesCount = 0;

        int currentTableIndex = 0;
        bool inTable = false;
        var currentTableRows = new List<(int LineNumber, string LineText, List<string> Cells, bool HasOuterPipes)>();

        void ProcessCurrentTable()
        {
            if (currentTableRows.Count == 0) return;

            totalTables++;
            currentTableIndex++;

            bool secondRowIsDelimiter = false;
            if (currentTableRows.Count >= 2)
            {
                var row1Cells = currentTableRows[1].Cells;
                if (row1Cells.Count > 0 && row1Cells.All(c => Regex.IsMatch(c, @"^:?-+:?$")))
                {
                    secondRowIsDelimiter = true;
                }
            }

            if (!secondRowIsDelimiter)
            {
                missingDelimiterTables++;
            }

            int headerCols = currentTableRows[0].Cells.Count;
            if (headerCols == 0) headerCols = 1;

            for (int r = 0; r < currentTableRows.Count; r++)
            {
                var row = currentTableRows[r];
                totalRows++;

                bool isDelimiter = (r == 1 && secondRowIsDelimiter);
                int actualCols = row.Cells.Count;
                bool isMismatched = !isDelimiter && (actualCols != headerCols);
                if (isMismatched) mismatchedRows++;

                if (!row.HasOuterPipes) unbalancedPipesCount++;

                string rec;
                if (r == 0)
                {
                    rec = isMismatched
                        ? $"Table #{currentTableIndex} header defines {actualCols} column{(actualCols == 1 ? "" : "s")}."
                        : $"Table #{currentTableIndex} header with {actualCols} columns.";
                }
                else if (isDelimiter)
                {
                    rec = actualCols != headerCols
                        ? $"Delimiter row defines {actualCols} columns but header has {headerCols}; align column counts."
                        : $"Valid delimiter alignment row with {actualCols} columns.";
                }
                else
                {
                    if (actualCols < headerCols)
                    {
                        rec = $"Row underflow: {actualCols} cell{(actualCols == 1 ? "" : "s")} (expected {headerCols}); missing cell separators or accidental line break.";
                    }
                    else if (actualCols > headerCols)
                    {
                        rec = $"Row overflow: {actualCols} cells (expected {headerCols}); escape unescaped pipe (\\|) in cell text.";
                    }
                    else if (!row.HasOuterPipes)
                    {
                        rec = "Missing leading or trailing outer pipe (|) for consistent table grid style.";
                    }
                    else
                    {
                        rec = "Consistent cell column count and alignment.";
                    }
                }

                items.Add(new MarkdownTableCellRowItem(
                    row.LineNumber,
                    currentTableIndex,
                    headerCols,
                    actualCols,
                    row.HasOuterPipes,
                    isMismatched,
                    rec));
            }

            currentTableRows.Clear();
            inTable = false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                insideCodeFence = !insideCodeFence;
                if (inTable) ProcessCurrentTable();
                continue;
            }

            if (insideCodeFence) continue;

            bool looksLikeTableRow = !string.IsNullOrWhiteSpace(trimmed) && trimmed.Contains('|');

            if (looksLikeTableRow)
            {
                if (!inTable)
                {
                    inTable = true;
                }

                bool hasOuter = trimmed.StartsWith("|") && trimmed.EndsWith("|") && !trimmed.EndsWith("\\|");
                var cells = SplitTableCells(trimmed);
                currentTableRows.Add((i + 1, line, cells, hasOuter));
            }
            else
            {
                if (inTable)
                {
                    ProcessCurrentTable();
                }
            }
        }

        if (inTable)
        {
            ProcessCurrentTable();
        }

        if (totalTables == 0)
        {
            string emptySummary = FormatTableCellWrappingSummary(0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownTableCellAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTableCellRowItem>(),
                "Clean Baseline", emptySummary);
        }

        double score = 100.0;
        if (mismatchedRows > 0) score -= mismatchedRows * 15.0;
        if (missingDelimiterTables > 0) score -= missingDelimiterTables * 20.0;
        if (unbalancedPipesCount > 0) score -= Math.Min(30.0, unbalancedPipesCount * 3.0);
        score = Math.Clamp(score, 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 => "Flawless Table Grid Structure",
            >= 80.0 => "Consistent Column Layout",
            >= 60.0 => "Mismatched Cell Counts",
            _ => "Malformed Table Syntax"
        };

        string summary = FormatTableCellWrappingSummary(
            totalTables, totalRows, mismatchedRows, missingDelimiterTables, unbalancedPipesCount, score, category);

        return new MarkdownTableCellAudit(
            totalTables, totalRows, mismatchedRows, missingDelimiterTables, unbalancedPipesCount, score, items, category, summary);
    }

    private static List<string> SplitTableCells(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool inBacktick = false;
        bool isEscaped = false;

        string trimmed = line.Trim();
        bool hasLeadingPipe = trimmed.StartsWith("|");
        bool hasTrailingPipe = trimmed.EndsWith("|") && !trimmed.EndsWith("\\|");

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (isEscaped)
            {
                sb.Append(c);
                isEscaped = false;
                continue;
            }

            if (c == '\\')
            {
                isEscaped = true;
                sb.Append(c);
                continue;
            }

            if (c == '`')
            {
                inBacktick = !inBacktick;
                sb.Append(c);
                continue;
            }

            if (c == '|' && !inBacktick)
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }
        cells.Add(sb.ToString().Trim());

        if (hasLeadingPipe && cells.Count > 0 && string.IsNullOrWhiteSpace(cells[0]))
        {
            cells.RemoveAt(0);
        }
        if (hasTrailingPipe && cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1]))
        {
            cells.RemoveAt(cells.Count - 1);
        }

        return cells;
    }

    public static string FormatTableCellWrappingSummary(string? markdownText)
        => AuditMarkdownTableCellWrapping(markdownText).Summary;

    public static string FormatTableCellWrappingSummary(
        int totalTables, int totalRows, int mismatchedRows, int missingDelimiters,
        int unbalancedPipes, double score, string category)
    {
        if (totalTables == 0)
            return "Markdown table cell audit: Clean document · 0 tables detected";

        var parts = new List<string>();
        parts.Add($"{totalTables} table{(totalTables == 1 ? "" : "s")}, {totalRows} row{(totalRows == 1 ? "" : "s")}");
        if (mismatchedRows > 0) parts.Add($"🚨 {mismatchedRows} mismatched column row{(mismatchedRows == 1 ? "" : "s")}");
        if (missingDelimiters > 0) parts.Add($"⚠ {missingDelimiters} missing delimiter row{(missingDelimiters == 1 ? "" : "s")}");
        if (unbalancedPipes > 0) parts.Add($"ℹ {unbalancedPipes} unbordered pipe row{(unbalancedPipes == 1 ? "" : "s")}");
        if (mismatchedRows == 0 && missingDelimiters == 0) parts.Add("100% columns aligned");

        string details = string.Join(" · ", parts);
        return $"Markdown table cell audit ({details} · {category} · {score:0.#}% hygiene score)";
    }

    public static MarkdownFootnoteBidirectionalAudit AuditMarkdownFootnoteBidirectionalLinks(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatFootnoteLinksSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownFootnoteBidirectionalAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownFootnoteBidirectionalItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownFootnoteBidirectionalItem>();

        var defRegex = new Regex(@"^[ ]{0,3}\[\^([a-zA-Z0-9_\-.:]+)\]:\s*(.*)$", RegexOptions.Compiled);
        var refRegex = new Regex(@"\[\^([a-zA-Z0-9_\-.:]+)\](?!\:)", RegexOptions.Compiled);

        var defs = new Dictionary<string, List<(int LineNumber, string Text)>>(StringComparer.OrdinalIgnoreCase);
        var refs = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        bool inCodeBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }
            if (inCodeBlock) continue;

            var defMatch = defRegex.Match(line);
            if (defMatch.Success)
            {
                var id = defMatch.Groups[1].Value;
                var content = defMatch.Groups[2].Value;
                if (!defs.TryGetValue(id, out var defInstances))
                {
                    defInstances = new List<(int, string)>();
                    defs[id] = defInstances;
                }
                defInstances.Add((i + 1, content));
                continue;
            }

            var refMatches = refRegex.Matches(line);
            foreach (Match rm in refMatches)
            {
                var id = rm.Groups[1].Value;
                if (!refs.TryGetValue(id, out var refInstances))
                {
                    refInstances = new List<int>();
                    refs[id] = refInstances;
                }
                refInstances.Add(i + 1);
            }
        }

        int totalDefs = defs.Values.Sum(v => v.Count);
        int totalRefs = refs.Values.Sum(v => v.Count);
        int resolvedPairs = 0;
        int missingDefs = 0;
        int unreferencedDefs = 0;
        int duplicateDefs = 0;
        int circularRefs = 0;

        foreach (var kvp in defs)
        {
            var id = kvp.Key;
            var instances = kvp.Value;
            bool isDup = instances.Count > 1;
            if (isDup) duplicateDefs += (instances.Count - 1);

            bool isReferenced = refs.ContainsKey(id);
            if (!isReferenced) unreferencedDefs += instances.Count;

            foreach (var inst in instances)
            {
                bool isCircular = refRegex.Matches(inst.Text).Cast<Match>().Any(m => string.Equals(m.Groups[1].Value, id, StringComparison.OrdinalIgnoreCase));
                if (isCircular) circularRefs++;

                string rec = "Valid footnote definition.";
                if (isCircular) rec = $"Eliminate recursive self-reference [^{id}] inside definition.";
                else if (isDup) rec = $"Deduplicate definition [^{id}]:, ensure single unique anchor.";
                else if (!isReferenced) rec = $"Reference [^{id}] in body text or remove orphan definition.";

                items.Add(new MarkdownFootnoteBidirectionalItem(
                    inst.LineNumber,
                    id,
                    "Definition",
                    isReferenced && !isCircular && !isDup,
                    false,
                    !isReferenced,
                    isDup,
                    isCircular,
                    inst.Text.Length > 60 ? inst.Text.Substring(0, 57) + "..." : inst.Text,
                    rec));
            }
        }

        foreach (var kvp in refs)
        {
            var id = kvp.Key;
            var lineNumbers = kvp.Value;
            bool hasDef = defs.ContainsKey(id);
            if (!hasDef) missingDefs += lineNumbers.Count;
            else if (!defs[id].Any(d => refRegex.Matches(d.Text).Cast<Match>().Any(m => string.Equals(m.Groups[1].Value, id, StringComparison.OrdinalIgnoreCase))))
            {
                resolvedPairs += lineNumbers.Count;
            }

            foreach (var lineNum in lineNumbers)
            {
                string rec = hasDef ? "Footnote link resolved." : $"Define missing footnote [^{id}]: at document bottom.";
                items.Add(new MarkdownFootnoteBidirectionalItem(
                    lineNum,
                    id,
                    "Reference",
                    hasDef,
                    !hasDef,
                    false,
                    false,
                    false,
                    $"[^{id}]",
                    rec));
            }
        }

        items.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));

        double score = 100.0;
        score -= missingDefs * 20.0;
        score -= duplicateDefs * 15.0;
        score -= circularRefs * 25.0;
        score -= unreferencedDefs * 5.0;
        score = Math.Clamp(score, 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 => "Impeccable Bidirectional Footnotes",
            >= 80.0 => "Sound Footnote Anchors",
            >= 60.0 => "Minor Footnote Discrepancies",
            _ => "Fragmented Footnote References"
        };

        string summary = FormatFootnoteLinksSummary(
            totalRefs, totalDefs, resolvedPairs, missingDefs,
            unreferencedDefs, duplicateDefs, circularRefs, score, category);

        return new MarkdownFootnoteBidirectionalAudit(
            totalRefs, totalDefs, resolvedPairs, missingDefs,
            unreferencedDefs, duplicateDefs, circularRefs, score,
            items, category, summary);
    }

    public static string FormatFootnoteLinksSummary(string? markdownText)
        => AuditMarkdownFootnoteBidirectionalLinks(markdownText).Summary;

    public static string FormatFootnoteLinksSummary(
        int totalRefs, int totalDefs, int resolvedPairs, int missingDefs,
        int unreferencedDefs, int duplicateDefs, int circularRefs, double score, string category)
    {
        if (totalRefs == 0 && totalDefs == 0)
            return "Markdown footnote bidirectional link audit: Clean document · 0 footnote references detected";

        var parts = new List<string>();
        parts.Add($"{totalRefs} reference{(totalRefs == 1 ? "" : "s")}, {totalDefs} definition{(totalDefs == 1 ? "" : "s")}");
        if (resolvedPairs > 0) parts.Add($"✓ {resolvedPairs} resolved");
        if (missingDefs > 0) parts.Add($"🚨 {missingDefs} missing definition{(missingDefs == 1 ? "" : "s")}");
        if (duplicateDefs > 0) parts.Add($"⚠ {duplicateDefs} duplicate definition{(duplicateDefs == 1 ? "" : "s")}");
        if (circularRefs > 0) parts.Add($"🚨 {circularRefs} circular reference{(circularRefs == 1 ? "" : "s")}");
        if (unreferencedDefs > 0) parts.Add($"ℹ {unreferencedDefs} unreferenced orphan{(unreferencedDefs == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown footnote bidirectional link audit ({details} · {category} · {score:0.#}% consistency score)";
    }


    public static MarkdownInlineHtmlAudit AuditMarkdownInlineHtmlSanitization(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatInlineHtmlSanitizationSummary(0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownInlineHtmlAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownInlineHtmlItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownInlineHtmlItem>();

        var dangerousTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script", "iframe", "object", "embed", "applet", "style", "link", "form", "input", "button", "meta", "base"
        };

        var deprecatedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "font", "center", "marquee", "blink", "big", "strike", "tt"
        };

        var safeInlinePairedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "b", "strong", "i", "em", "u", "s", "del", "sub", "sup", "span", "code", "kbd", "mark", "small", "details", "summary", "p", "div", "table", "thead", "tbody", "tr", "th", "td", "ul", "ol", "li", "blockquote", "a", "h1", "h2", "h3", "h4", "h5", "h6"
        };

        var selfClosingTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "br", "hr", "img", "wbr", "col", "source", "track", "area"
        };

        int totalConstructs = 0;
        int allowedSafeTags = 0;
        int dangerousTagCount = 0;
        int unescapedRawCount = 0;
        int deprecatedTagCount = 0;

        var openTagStack = new Stack<(string TagName, int LineNumber, int Column)>();
        bool inCodeFence = false;

        var autolinkRegex = new Regex(@"<(?:https?://[^\s>]+|mailto:[^\s>]+|[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,})>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        var htmlTagRegex = new Regex(@"<(/?)([a-zA-Z0-9\-]+)([^>]*)>", RegexOptions.Compiled);
        var inlineCodeRegex = new Regex(@"`[^`]+`", RegexOptions.Compiled);

        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            string line = lines[lineIdx];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence)
                continue;

            // Mask inline code with spaces
            string scrubbed = inlineCodeRegex.Replace(line, m => new string(' ', m.Length));

            // Mask autolinks with spaces so they aren't parsed as HTML
            scrubbed = autolinkRegex.Replace(scrubbed, m => new string(' ', m.Length));

            // Find all HTML tags on this line
            var matches = htmlTagRegex.Matches(scrubbed);
            var tagSpans = new List<(int Index, int Length)>();

            foreach (Match m in matches)
            {
                totalConstructs++;
                tagSpans.Add((m.Index, m.Length));

                bool isClosing = m.Groups[1].Length > 0;
                string tagName = m.Groups[2].Value.ToLowerInvariant();
                string attributes = m.Groups[3].Value;
                int col = m.Index + 1;

                bool hasEventHandler = Regex.IsMatch(attributes, @"\bon[a-zA-Z]+\s*=", RegexOptions.IgnoreCase);

                if (dangerousTags.Contains(tagName) || hasEventHandler)
                {
                    dangerousTagCount++;
                    items.Add(new MarkdownInlineHtmlItem(
                        lineIdx + 1, col, m.Value,
                        "DangerousTag", tagName,
                        line.Trim(),
                        $"Remove or sanitize potentially dangerous HTML construct <{m.Groups[1].Value}{tagName}> with event handlers or scripts to prevent XSS."));
                }
                else if (deprecatedTags.Contains(tagName))
                {
                    deprecatedTagCount++;
                    items.Add(new MarkdownInlineHtmlItem(
                        lineIdx + 1, col, m.Value,
                        "DeprecatedTag", tagName,
                        line.Trim(),
                        $"Replace obsolete HTML tag <{tagName}> with modern CSS styling or semantic Markdown alternatives."));
                }
                else
                {
                    allowedSafeTags++;
                    if (!isClosing && !selfClosingTags.Contains(tagName) && !attributes.EndsWith("/"))
                    {
                        if (safeInlinePairedTags.Contains(tagName))
                        {
                            openTagStack.Push((tagName, lineIdx + 1, col));
                        }
                    }
                    else if (isClosing)
                    {
                        if (openTagStack.Count > 0 && openTagStack.Peek().TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            openTagStack.Pop();
                        }
                    }
                }
            }

            // Remove matched tags from scrubbed to check for remaining raw < or >
            var sb = new StringBuilder(scrubbed);
            foreach (var span in tagSpans.OrderByDescending(s => s.Index))
            {
                for (int i = 0; i < span.Length; i++)
                {
                    sb[span.Index + i] = ' ';
                }
            }
            string remaining = sb.ToString();

            // Look for stray < and > in remaining text
            for (int i = 0; i < remaining.Length; i++)
            {
                char c = remaining[i];
                if (c == '<' || c == '>')
                {
                    // Check if part of markdown blockquote (> at start of line)
                    if (c == '>' && (i == 0 || string.IsNullOrWhiteSpace(remaining.Substring(0, i))))
                        continue;

                    // Check if part of arrow like -> or =>
                    if (c == '>' && i > 0 && (remaining[i - 1] == '-' || remaining[i - 1] == '='))
                        continue;
                    if (c == '<' && i < remaining.Length - 1 && (remaining[i + 1] == '-' || remaining[i + 1] == '='))
                        continue;

                    totalConstructs++;
                    unescapedRawCount++;
                    int col = i + 1;
                    int start = Math.Max(0, i - 15);
                    int len = Math.Min(remaining.Length - start, 30);
                    string snippet = remaining.Substring(start, len).Trim();

                    items.Add(new MarkdownInlineHtmlItem(
                        lineIdx + 1, col, c.ToString(),
                        "UnescapedRawAngleBracket", "",
                        line.Trim(),
                        $"Escape unescaped raw angle bracket '{c}' as &{(c == '<' ? "lt" : "gt")}; to avoid Markdown parsing ambiguity."));
                }
            }
        }

        int unclosedCount = openTagStack.Count;
        while (openTagStack.Count > 0)
        {
            var open = openTagStack.Pop();
            items.Add(new MarkdownInlineHtmlItem(
                open.LineNumber, open.Column, $"<{open.TagName}>",
                "UnclosedInlineTag", open.TagName,
                lines[open.LineNumber - 1].Trim(),
                $"Provide matching closing tag </{open.TagName}> for unclosed inline tag."));
        }

        double score = 100.0 - (dangerousTagCount * 25.0) - (unescapedRawCount * 10.0) - (unclosedCount * 8.0) - (deprecatedTagCount * 5.0);
        score = Math.Clamp(score, 0.0, 100.0);

        int totalIssues = dangerousTagCount + unescapedRawCount + unclosedCount + deprecatedTagCount;
        string category = score switch
        {
            100.0 when totalIssues == 0 => "Clean Baseline",
            >= 85.0 => "High Hygiene",
            >= 70.0 => "Minor HTML Warnings",
            >= 50.0 => "Risky Unescaped Content",
            _ => "Critical Security Hazard"
        };

        string summary = FormatInlineHtmlSanitizationSummary(
            totalConstructs, allowedSafeTags, dangerousTagCount,
            unescapedRawCount, unclosedCount, deprecatedTagCount,
            score, category);

        return new MarkdownInlineHtmlAudit(
            totalConstructs, allowedSafeTags, dangerousTagCount,
            unescapedRawCount, unclosedCount, deprecatedTagCount,
            score, items, category, summary);
    }

    public static string FormatInlineHtmlSanitizationSummary(string? markdownText)
        => AuditMarkdownInlineHtmlSanitization(markdownText).Summary;

    public static string FormatInlineHtmlSanitizationSummary(
        int totalConstructs, int allowedSafe, int dangerous, int unescapedRaw,
        int unclosed, int deprecated, double score, string category)
    {
        if (totalConstructs == 0)
            return "Markdown inline HTML & angle bracket sanitization audit: Clean document · 0 HTML tags or unescaped angle brackets detected";

        var parts = new List<string>();
        parts.Add($"{totalConstructs} construct{(totalConstructs == 1 ? "" : "s")}");
        if (allowedSafe > 0) parts.Add($"✓ {allowedSafe} safe");
        if (dangerous > 0) parts.Add($"🚨 {dangerous} dangerous tag{(dangerous == 1 ? "" : "s")}");
        if (unescapedRaw > 0) parts.Add($"⚠ {unescapedRaw} unescaped bracket{(unescapedRaw == 1 ? "" : "s")}");
        if (unclosed > 0) parts.Add($"⚠ {unclosed} unclosed tag{(unclosed == 1 ? "" : "s")}");
        if (deprecated > 0) parts.Add($"ℹ {deprecated} deprecated tag{(deprecated == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown inline HTML sanitization audit ({details} · {category} · {score:0.#}% hygiene score)";
    }

    public static MarkdownTaskFormattingAudit AuditMarkdownTaskListFormatting(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatTaskListFormattingSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownTaskFormattingAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTaskFormattingItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownTaskFormattingItem>();

        int completed = 0;
        int pending = 0;
        int inconsistentIndent = 0;
        int malformed = 0;
        int orphaned = 0;
        int maxNesting = 0;
        bool inCodeBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }
            if (inCodeBlock || string.IsNullOrWhiteSpace(line)) continue;

            var standardMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"^(?<indent>[ \t]*)(?<bullet>[-*+]|\d+\.)\s+\[(?<state>[ xX])\](?<rest>.*)$");

            if (standardMatch.Success)
            {
                string rawIndent = standardMatch.Groups["indent"].Value;
                int indentSpaces = rawIndent.Replace("\t", "    ").Length;
                int nesting = indentSpaces / 2;
                if (nesting > maxNesting) maxNesting = nesting;

                string state = standardMatch.Groups["state"].Value;
                bool isCompleted = state.Equals("x", StringComparison.OrdinalIgnoreCase);
                if (isCompleted) completed++; else pending++;

                bool hasIndentIssue = false;
                if (indentSpaces > 0 && (indentSpaces % 2 != 0))
                {
                    hasIndentIssue = true;
                    inconsistentIndent++;
                }

                items.Add(new MarkdownTaskFormattingItem(
                    i + 1,
                    trimmed,
                    $"[{state}]",
                    indentSpaces,
                    nesting,
                    isCompleted,
                    hasIndentIssue,
                    false,
                    false,
                    hasIndentIssue ? $"Indentation of {indentSpaces} spaces is irregular (recommended: multiples of 2 or 4)" : "Valid task list item"));
            }
            else
            {
                var malformedMatch = System.Text.RegularExpressions.Regex.Match(
                    line, @"^(?<indent>[ \t]*)(?<bullet>[-*+]|\d+\.)(?<sep>\s*)\[(?<state>[^\]]*)\](?<rest>.*)$");
                if (malformedMatch.Success)
                {
                    string rawIndent = malformedMatch.Groups["indent"].Value;
                    int indentSpaces = rawIndent.Replace("\t", "    ").Length;
                    string sep = malformedMatch.Groups["sep"].Value;
                    string state = malformedMatch.Groups["state"].Value;

                    malformed++;
                    string issue = sep.Length == 0
                        ? "Missing space between bullet marker and checkbox bracket"
                        : $"Invalid checkbox state '[{state}]' (expected ' ' or 'x'/'X')";

                    items.Add(new MarkdownTaskFormattingItem(
                        i + 1,
                        trimmed,
                        $"[{state}]",
                        indentSpaces,
                        indentSpaces / 2,
                        false,
                        false,
                        true,
                        false,
                        issue));
                }
                else
                {
                    var orphanedMatch = System.Text.RegularExpressions.Regex.Match(
                        line, @"^(?<indent>[ \t]*)\[(?<state>[ xX])\](?<rest>.*)$");
                    if (orphanedMatch.Success)
                    {
                        string rawIndent = orphanedMatch.Groups["indent"].Value;
                        int indentSpaces = rawIndent.Replace("\t", "    ").Length;
                        string state = orphanedMatch.Groups["state"].Value;

                        orphaned++;
                        items.Add(new MarkdownTaskFormattingItem(
                            i + 1,
                            trimmed,
                            $"[{state}]",
                            indentSpaces,
                            0,
                            state.Equals("x", StringComparison.OrdinalIgnoreCase),
                            false,
                            false,
                            true,
                            "Orphaned checkbox without list bullet marker ('-', '*', or '+')"));
                    }
                }
            }
        }

        int totalTasks = completed + pending + malformed + orphaned;
        if (totalTasks == 0)
        {
            string emptySummary = FormatTaskListFormattingSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownTaskFormattingAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTaskFormattingItem>(),
                "Clean Baseline", emptySummary);
        }

        double penalty = (malformed * 15.0) + (orphaned * 10.0) + (inconsistentIndent * 5.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 => "High Consistency",
            >= 75.0 => "Moderate Inconsistencies",
            _ => "Formatting Inconsistencies Detected"
        };

        string summary = FormatTaskListFormattingSummary(
            totalTasks, completed, pending, inconsistentIndent,
            malformed, orphaned, maxNesting, score, category);

        return new MarkdownTaskFormattingAudit(
            totalTasks, completed, pending, inconsistentIndent,
            malformed, orphaned, maxNesting, score, items, category, summary);
    }

    public static string FormatTaskListFormattingSummary(string? markdownText)
        => AuditMarkdownTaskListFormatting(markdownText).Summary;

    public static string FormatTaskListFormattingSummary(
        int totalTasks, int completed, int pending, int inconsistentIndent,
        int malformed, int orphaned, int maxNesting, double score, string category)
    {
        if (totalTasks == 0)
            return "Markdown task list formatting audit: Clean document \u00b7 0 task checkboxes detected";

        var parts = new List<string>();
        parts.Add($"{totalTasks} task{(totalTasks == 1 ? "" : "s")}");
        if (completed > 0) parts.Add($"\u2713 {completed} completed");
        if (pending > 0) parts.Add($"\u23f3 {pending} pending");
        if (inconsistentIndent > 0) parts.Add($"\u26a0 {inconsistentIndent} irregular indent{(inconsistentIndent == 1 ? "" : "s")}");
        if (malformed > 0) parts.Add($"\ud83d\udea8 {malformed} malformed");
        if (orphaned > 0) parts.Add($"\u26a0 {orphaned} orphaned");
        if (maxNesting > 0) parts.Add($"{maxNesting} tier{(maxNesting == 1 ? "" : "s")} deep");

        string details = string.Join(" \u00b7 ", parts);
        return $"Markdown task list formatting audit ({details} \u00b7 {category} \u00b7 {score:0.#}% hygiene score)";
    }

    public static MarkdownTablePipeAlignmentAudit AuditMarkdownTablePipeAlignment(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatTablePipeAlignmentSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownTablePipeAlignmentAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTablePipeAlignmentItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownTablePipeAlignmentItem>();

        int totalTables = 0;
        int totalRows = 0;
        int alignedTables = 0;
        int misalignedDelimiterRows = 0;
        int columnCountMismatchRows = 0;
        int missingHeaderHyphenRows = 0;
        int unescapedPipesInCodeCount = 0;

        bool insideCodeFence = false;
        var currentTableLines = new List<(int LineNumber, string Line)>();

        void ProcessCurrentTable()
        {
            if (currentTableLines.Count < 2)
            {
                currentTableLines.Clear();
                return;
            }

            totalTables++;
            bool tableHasIssues = false;
            int tableIndex = totalTables;

            var parsedRows = new List<(int LineNumber, string RawLine, List<string> Cells, bool HasOuterPipes)>();
            for (int r = 0; r < currentTableLines.Count; r++)
            {
                var tl = currentTableLines[r];
                var (cells, hasOuter, unescapedPipes) = ParseTableCellsWithCodeGuard(tl.Line);
                if (unescapedPipes > 0)
                {
                    unescapedPipesInCodeCount += unescapedPipes;
                    tableHasIssues = true;
                }
                parsedRows.Add((tl.LineNumber, tl.Line, cells, hasOuter));
            }

            totalRows += parsedRows.Count;
            int expectedCols = parsedRows[0].Cells.Count;

            items.Add(new MarkdownTablePipeAlignmentItem(
                parsedRows[0].LineNumber, tableIndex, "Header", expectedCols, expectedCols,
                false, false, false,
                parsedRows[0].RawLine.Trim(),
                "Header row defines table column basis"));

            var delimRow = parsedRows[1];
            bool hasMissingHyphens = false;
            bool isDelimiterMismatch = false;

            if (delimRow.Cells.Count != expectedCols)
            {
                isDelimiterMismatch = true;
                misalignedDelimiterRows++;
                tableHasIssues = true;
            }

            foreach (var cell in delimRow.Cells)
            {
                string c = cell.Trim();
                string stripped = c.Trim(':');
                if (stripped.Length < 3 || !stripped.All(ch => ch == '-'))
                {
                    hasMissingHyphens = true;
                    break;
                }
            }

            if (hasMissingHyphens)
            {
                missingHeaderHyphenRows++;
                tableHasIssues = true;
            }

            items.Add(new MarkdownTablePipeAlignmentItem(
                delimRow.LineNumber, tableIndex, "Delimiter", delimRow.Cells.Count, expectedCols,
                hasMissingHyphens, false, isDelimiterMismatch,
                delimRow.RawLine.Trim(),
                isDelimiterMismatch ? $"Delimiter row column count ({delimRow.Cells.Count}) does not match header ({expectedCols})" :
                hasMissingHyphens ? "Delimiter row lacks required separator hyphens (---)" : "Delimiter row aligned"));

            for (int r = 2; r < parsedRows.Count; r++)
            {
                var dataRow = parsedRows[r];
                bool mismatch = dataRow.Cells.Count != expectedCols;
                if (mismatch)
                {
                    columnCountMismatchRows++;
                    tableHasIssues = true;
                }

                items.Add(new MarkdownTablePipeAlignmentItem(
                    dataRow.LineNumber, tableIndex, "Data", dataRow.Cells.Count, expectedCols,
                    false, false, mismatch,
                    dataRow.RawLine.Trim(),
                    mismatch ? $"Data row has {dataRow.Cells.Count} columns; expected {expectedCols}" : "Data row aligned"));
            }

            if (!tableHasIssues)
            {
                alignedTables++;
            }

            currentTableLines.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                insideCodeFence = !insideCodeFence;
                if (insideCodeFence && currentTableLines.Count > 0)
                {
                    ProcessCurrentTable();
                }
                continue;
            }

            if (insideCodeFence) continue;

            if (trimmed.Contains("|"))
            {
                currentTableLines.Add((i + 1, line));
            }
            else
            {
                if (currentTableLines.Count > 0)
                {
                    ProcessCurrentTable();
                }
            }
        }

        if (currentTableLines.Count > 0)
        {
            ProcessCurrentTable();
        }

        if (totalTables == 0)
        {
            string emptySummary = FormatTablePipeAlignmentSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownTablePipeAlignmentAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTablePipeAlignmentItem>(),
                "Clean Baseline", emptySummary);
        }

        double penalty = (columnCountMismatchRows * 15.0) +
                         (misalignedDelimiterRows * 20.0) +
                         (missingHeaderHyphenRows * 25.0) +
                         (unescapedPipesInCodeCount * 5.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = score switch
        {
            >= 90.0 => "High Alignment",
            >= 70.0 => "Moderate Misalignment",
            _ => "Severe Delimiter Inconsistency"
        };

        string summary = FormatTablePipeAlignmentSummary(
            totalTables, totalRows, alignedTables, misalignedDelimiterRows,
            columnCountMismatchRows, missingHeaderHyphenRows, unescapedPipesInCodeCount,
            score, category);

        return new MarkdownTablePipeAlignmentAudit(
            totalTables, totalRows, alignedTables, misalignedDelimiterRows,
            columnCountMismatchRows, missingHeaderHyphenRows, unescapedPipesInCodeCount,
            score, items, category, summary);
    }

    private static (List<string> Cells, bool HasOuterPipes, int UnescapedPipes) ParseTableCellsWithCodeGuard(string line)
    {
        var cells = new List<string>();
        string trimmed = line.Trim();
        bool hasOuter = trimmed.StartsWith("|") && trimmed.EndsWith("|") && !trimmed.EndsWith(@"\|");

        if (trimmed.StartsWith("|")) trimmed = trimmed.Substring(1);
        if (trimmed.EndsWith("|") && !trimmed.EndsWith(@"\|")) trimmed = trimmed.Substring(0, trimmed.Length - 1);

        var currentCell = new System.Text.StringBuilder();
        bool insideInlineCode = false;
        int unescapedPipesInCode = 0;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char ch = trimmed[i];
            if (ch == '\\' && i + 1 < trimmed.Length && trimmed[i + 1] == '|')
            {
                currentCell.Append('|');
                i++;
                continue;
            }

            if (ch == '`')
            {
                insideInlineCode = !insideInlineCode;
                currentCell.Append(ch);
                continue;
            }

            if (ch == '|' && insideInlineCode)
            {
                unescapedPipesInCode++;
                currentCell.Append(ch);
                continue;
            }

            if (ch == '|' && !insideInlineCode)
            {
                cells.Add(currentCell.ToString().Trim());
                currentCell.Clear();
                continue;
            }

            currentCell.Append(ch);
        }

        cells.Add(currentCell.ToString().Trim());
        return (cells, hasOuter, unescapedPipesInCode);
    }

    public static string FormatTablePipeAlignmentSummary(string? markdownText)
        => AuditMarkdownTablePipeAlignment(markdownText).Summary;

    public static string FormatTablePipeAlignmentSummary(
        int totalTables, int totalRows, int alignedTables, int misalignedDelimiters,
        int colMismatches, int missingHyphens, int unescapedPipes, double score, string category)
    {
        if (totalTables == 0)
            return "Markdown table pipe alignment audit: Clean document · 0 tables detected";

        var parts = new List<string>();
        parts.Add($"{totalTables} table{(totalTables == 1 ? "" : "s")}");
        if (alignedTables > 0) parts.Add($"\u2713 {alignedTables} aligned");
        if (misalignedDelimiters > 0) parts.Add($"\ud83d\udea8 {misalignedDelimiters} misaligned delimiter row{(misalignedDelimiters == 1 ? "" : "s")}");
        if (colMismatches > 0) parts.Add($"\u26a0 {colMismatches} column mismatch{(colMismatches == 1 ? "" : "es")}");
        if (missingHyphens > 0) parts.Add($"\u26a0 {missingHyphens} missing header hyphen{(missingHyphens == 1 ? "" : "s")}");
        if (unescapedPipes > 0) parts.Add($"\u26a0 {unescapedPipes} unescaped code pipe{(unescapedPipes == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown table pipe alignment audit ({details} · {category} · {score:0.#}% alignment score)";
    }



    public static string FormatDefinitionListsSummary(string? markdownText)
        => AuditMarkdownDefinitionLists(markdownText).Summary;

    public static string FormatDefinitionListsSummary(
        int totalTerms, int totalDefinitions, int multiLineDefinitions,
        int duplicateTerms, int orphanDefinitions, double score, string category)
    {
        if (totalDefinitions == 0)
            return "Markdown definition list audit: Clean document · 0 glossary definitions detected";

        var parts = new List<string>();
        parts.Add($"{totalTerms} term{(totalTerms == 1 ? "" : "s")}");
        parts.Add($"{totalDefinitions} definition{(totalDefinitions == 1 ? "" : "s")}");
        if (multiLineDefinitions > 0) parts.Add($"{multiLineDefinitions} multi-line");
        if (duplicateTerms > 0) parts.Add($"⚠ {duplicateTerms} duplicate term{(duplicateTerms == 1 ? "" : "s")}");
        if (orphanDefinitions > 0) parts.Add($"🚨 {orphanDefinitions} orphan definition{(orphanDefinitions == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown definition list audit ({details} · {category} · {score:0.#}% hygiene score)";
    }


    public static MarkdownFootnoteParityAudit AuditMarkdownFootnoteDefinitionParity(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatFootnoteDefinitionParitySummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownFootnoteParityAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownFootnoteParityItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownFootnoteParityItem>();

        var defs = new Dictionary<string, List<(int LineNumber, string Excerpt)>>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<(string Label, int LineNumber, string Excerpt)>();
        var defLabelsInOrder = new List<string>();
        var refLabelsInOrder = new List<string>();

        bool insideCodeFence = false;
        var defRegex = new System.Text.RegularExpressions.Regex(@"^[ ]{0,3}\[\^([^\]]+)\]:\s*(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
        var refRegex = new System.Text.RegularExpressions.Regex(@"(?<!\[\^)\[\^([^\]]+)\](?!:)", System.Text.RegularExpressions.RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                insideCodeFence = !insideCodeFence;
                continue;
            }

            if (insideCodeFence) continue;

            var defMatch = defRegex.Match(line);
            if (defMatch.Success)
            {
                string label = defMatch.Groups[1].Value.Trim();
                string excerpt = line.Length > 80 ? line.Substring(0, 80) + "..." : line;

                if (!defs.ContainsKey(label))
                {
                    defs[label] = new List<(int, string)>();
                    defLabelsInOrder.Add(label);
                }
                defs[label].Add((i + 1, excerpt));
            }
            else
            {
                var matches = refRegex.Matches(line);
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    string label = m.Groups[1].Value.Trim();
                    string excerpt = line.Length > 80 ? line.Substring(0, 80) + "..." : line;
                    refs.Add((label, i + 1, excerpt));
                    refLabelsInOrder.Add(label);
                }
            }
        }

        int totalRefs = refs.Count;
        int totalDefs = defs.Values.Sum(list => list.Count);

        if (totalRefs == 0 && totalDefs == 0)
        {
            string emptySummary = FormatFootnoteDefinitionParitySummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownFootnoteParityAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownFootnoteParityItem>(),
                "Clean Baseline", emptySummary);
        }

        int missingDefCount = 0;
        int orphanDefCount = 0;
        int duplicateDefCount = 0;
        int nonSequentialNumericCount = 0;

        var refSet = new HashSet<string>(refs.Select(r => r.Label), StringComparer.OrdinalIgnoreCase);

        foreach (var r in refs)
        {
            bool hasDef = defs.ContainsKey(r.Label);
            if (!hasDef)
            {
                missingDefCount++;
                items.Add(new MarkdownFootnoteParityItem(
                    r.Label, r.LineNumber,
                    IsDefinition: false,
                    IsMissingDefinition: true,
                    IsOrphanDefinition: false,
                    IsDuplicateDefinition: false,
                    IsNonSequentialNumeric: false,
                    r.Excerpt,
                    $"Define missing footnote [^{r.Label}]: at bottom of document."));
            }
            else
            {
                items.Add(new MarkdownFootnoteParityItem(
                    r.Label, r.LineNumber,
                    IsDefinition: false,
                    IsMissingDefinition: false,
                    IsOrphanDefinition: false,
                    IsDuplicateDefinition: false,
                    IsNonSequentialNumeric: false,
                    r.Excerpt,
                    "Reference linked to definition."));
            }
        }

        foreach (var kvp in defs)
        {
            string label = kvp.Key;
            var defOccurrences = kvp.Value;
            bool isReferenced = refSet.Contains(label);

            if (!isReferenced)
            {
                orphanDefCount += defOccurrences.Count;
                foreach (var occ in defOccurrences)
                {
                    items.Add(new MarkdownFootnoteParityItem(
                        label, occ.LineNumber,
                        IsDefinition: true,
                        IsMissingDefinition: false,
                        IsOrphanDefinition: true,
                        IsDuplicateDefinition: defOccurrences.Count > 1,
                        IsNonSequentialNumeric: false,
                        occ.Excerpt,
                        $"Footnote [^{label}] is defined but never referenced in document body."));
                }
            }
            else
            {
                if (defOccurrences.Count > 1)
                {
                    duplicateDefCount += (defOccurrences.Count - 1);
                    for (int d = 0; d < defOccurrences.Count; d++)
                    {
                        var occ = defOccurrences[d];
                        items.Add(new MarkdownFootnoteParityItem(
                            label, occ.LineNumber,
                            IsDefinition: true,
                            IsMissingDefinition: false,
                            IsOrphanDefinition: false,
                            IsDuplicateDefinition: d > 0,
                            IsNonSequentialNumeric: false,
                            occ.Excerpt,
                            d > 0 ? $"Duplicate definition for footnote [^{label}]." : "Primary definition."));
                    }
                }
                else
                {
                    var occ = defOccurrences[0];
                    items.Add(new MarkdownFootnoteParityItem(
                        label, occ.LineNumber,
                        IsDefinition: true,
                        IsMissingDefinition: false,
                        IsOrphanDefinition: false,
                        IsDuplicateDefinition: false,
                        IsNonSequentialNumeric: false,
                        occ.Excerpt,
                        "Valid footnote definition."));
                }
            }
        }

        int expectedNumber = 1;
        foreach (var label in refLabelsInOrder)
        {
            if (int.TryParse(label, out int num))
            {
                if (num != expectedNumber)
                {
                    nonSequentialNumericCount++;
                }
                expectedNumber = Math.Max(expectedNumber, num) + 1;
            }
        }

        int balancedPairs = defs.Keys.Count(k => refSet.Contains(k) && defs[k].Count == 1);

        double penalty = (missingDefCount * 25.0) +
                         (orphanDefCount * 15.0) +
                         (duplicateDefCount * 20.0) +
                         (nonSequentialNumericCount * 5.0);
        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = score switch
        {
            >= 90.0 => "High Parity",
            >= 70.0 => "Moderate Discrepancy",
            _ => "Severe Reference Fracture"
        };

        string summary = FormatFootnoteDefinitionParitySummary(
            totalRefs, totalDefs, balancedPairs, missingDefCount, orphanDefCount,
            duplicateDefCount, nonSequentialNumericCount, score, category);

        return new MarkdownFootnoteParityAudit(
            totalRefs, totalDefs, balancedPairs, missingDefCount, orphanDefCount,
            duplicateDefCount, nonSequentialNumericCount, score, items, category, summary);
    }

    public static string FormatFootnoteDefinitionParitySummary(string? markdownText)
        => AuditMarkdownFootnoteDefinitionParity(markdownText).Summary;

    public static string FormatFootnoteDefinitionParitySummary(
        int totalRefs, int totalDefs, int balancedPairs, int missingDefs,
        int orphanDefs, int duplicateDefs, int nonSequential, double score, string category)
    {
        if (totalRefs == 0 && totalDefs == 0)
            return "Markdown footnote definition parity audit: Clean document · 0 footnotes detected";

        var parts = new List<string>();
        parts.Add($"{totalRefs} ref{(totalRefs == 1 ? "" : "s")}");
        parts.Add($"{totalDefs} def{(totalDefs == 1 ? "" : "s")}");
        if (balancedPairs > 0) parts.Add($"\u2713 {balancedPairs} balanced");
        if (missingDefs > 0) parts.Add($"\ud83d\udea8 {missingDefs} missing def{(missingDefs == 1 ? "" : "s")}");
        if (orphanDefs > 0) parts.Add($"\u26a0 {orphanDefs} orphan def{(orphanDefs == 1 ? "" : "s")}");
        if (duplicateDefs > 0) parts.Add($"\u26a0 {duplicateDefs} duplicate def{(duplicateDefs == 1 ? "" : "s")}");
        if (nonSequential > 0) parts.Add($"\u2139 {nonSequential} non-sequential");

        string details = string.Join(" · ", parts);
        return $"Markdown footnote definition parity audit ({details} · {category} · {score:0.#}% parity score)";
    }


    public static MarkdownHtmlSanitizationAudit AuditMarkdownHtmlSanitization(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatHtmlSanitizationSummary(0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownHtmlSanitizationAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownHtmlSanitizationItem>(),
                "Clean Baseline", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownHtmlSanitizationItem>();

        var dangerousTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script", "iframe", "object", "embed", "applet", "meta", "link", "form", "style", "base"
        };

        var deprecatedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "font", "center", "marquee", "blink", "strike", "tt", "big"
        };

        var pairedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "b", "i", "u", "s", "span", "div", "p", "table", "thead", "tbody", "tr", "th", "td",
            "ul", "ol", "li", "details", "summary", "blockquote", "code", "pre", "h1", "h2", "h3", "h4", "h5", "h6",
            "strong", "em", "del", "ins", "sub", "sup"
        };

        int totalTags = 0;
        int dangerousTagCount = 0;
        int inlineEventHandlerCount = 0;
        int deprecatedTagCount = 0;
        int unclosedTagCount = 0;

        var openTagStack = new Stack<(string TagName, int LineNumber)>();
        bool inCodeFence = false;

        var tagRegex = new Regex(@"<(/?)([a-zA-Z0-9\-]+)([^>]*)>", RegexOptions.Compiled);
        var inlineCodeRegex = new Regex(@"`[^`]+`", RegexOptions.Compiled);
        var autolinkRegex = new Regex(@"<(?:https?://[^\s>]+|mailto:[^\s>]+|[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,})>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                inCodeFence = !inCodeFence;
                continue;
            }
            if (inCodeFence) continue;

            string scrubbed = inlineCodeRegex.Replace(line, m => new string(' ', m.Length));
            scrubbed = autolinkRegex.Replace(scrubbed, m => new string(' ', m.Length));

            var matches = tagRegex.Matches(scrubbed);
            foreach (Match m in matches)
            {
                totalTags++;
                bool isClosing = m.Groups[1].Length > 0;
                string tagName = m.Groups[2].Value.ToLowerInvariant();
                string attributes = m.Groups[3].Value;
                int col = m.Index + 1;

                bool hasOnHandler = Regex.IsMatch(attributes, @"\bon[a-zA-Z]+\s*=", RegexOptions.IgnoreCase);
                bool hasJsUri = Regex.IsMatch(attributes, @"(?:href|src)\s*=\s*['""]?\s*javascript:", RegexOptions.IgnoreCase);

                if (dangerousTags.Contains(tagName))
                {
                    dangerousTagCount++;
                    items.Add(new MarkdownHtmlSanitizationItem(
                        i + 1, col, m.Value, "CriticalSecurity", tagName, line.Trim(),
                        $"Dangerous raw HTML <{tagName}> tag detected; replace with safe Markdown or sanitize for security."));
                }
                else if (hasOnHandler || hasJsUri)
                {
                    inlineEventHandlerCount++;
                    items.Add(new MarkdownHtmlSanitizationItem(
                        i + 1, col, m.Value, "CriticalSecurity", tagName, line.Trim(),
                        $"Inline event handler or javascript: URI in <{tagName}> tag detected; remove handler to prevent XSS."));
                }
                else if (deprecatedTags.Contains(tagName))
                {
                    deprecatedTagCount++;
                    items.Add(new MarkdownHtmlSanitizationItem(
                        i + 1, col, m.Value, "DeprecatedTag", tagName, line.Trim(),
                        $"Deprecated HTML tag <{tagName}> detected; replace with modern CSS or Markdown equivalent."));
                }
                else if (pairedTags.Contains(tagName))
                {
                    if (!isClosing && !attributes.EndsWith("/"))
                    {
                        openTagStack.Push((tagName, i + 1));
                    }
                    else if (isClosing)
                    {
                        if (openTagStack.Count > 0 && string.Equals(openTagStack.Peek().TagName, tagName, StringComparison.OrdinalIgnoreCase))
                        {
                            openTagStack.Pop();
                        }
                        else
                        {
                            unclosedTagCount++;
                            items.Add(new MarkdownHtmlSanitizationItem(
                                i + 1, col, m.Value, "UnclosedTag", tagName, line.Trim(),
                                $"Closing tag </{tagName}> does not match open tag stack; check document nesting."));
                        }
                    }
                }
            }
        }

        while (openTagStack.Count > 0)
        {
            var open = openTagStack.Pop();
            unclosedTagCount++;
            items.Add(new MarkdownHtmlSanitizationItem(
                open.LineNumber, 1, $"<{open.TagName}>", "UnclosedTag", open.TagName, $"<{open.TagName}>",
                $"Unclosed HTML tag <{open.TagName}> reaching end of document."));
        }

        if (totalTags == 0)
        {
            string emptySummary = FormatHtmlSanitizationSummary(0, 0, 0, 0, 0, 100.0, "Clean Baseline");
            return new MarkdownHtmlSanitizationAudit(
                0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownHtmlSanitizationItem>(),
                "Clean Baseline", emptySummary);
        }

        double penalty = (dangerousTagCount * 30.0) +
                         (inlineEventHandlerCount * 25.0) +
                         (unclosedTagCount * 10.0) +
                         (deprecatedTagCount * 5.0);

        double score = Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category = score switch
        {
            >= 90.0 => "High Security Hygiene",
            >= 70.0 => "Moderate HTML Warnings",
            _ => "Critical Security Hazards"
        };

        string summary = FormatHtmlSanitizationSummary(
            totalTags, dangerousTagCount, inlineEventHandlerCount, deprecatedTagCount, unclosedTagCount, score, category);

        return new MarkdownHtmlSanitizationAudit(
            totalTags, dangerousTagCount, inlineEventHandlerCount, deprecatedTagCount, unclosedTagCount, score, items, category, summary);
    }

    public static string FormatHtmlSanitizationSummary(string? markdownText)
        => AuditMarkdownHtmlSanitization(markdownText).Summary;

    public static string FormatHtmlSanitizationSummary(
        int totalTags, int dangerousTags, int eventHandlers, int deprecatedTags, int unclosedTags, double score, string category)
    {
        if (totalTags == 0)
            return "Markdown HTML sanitization audit: Clean document · 0 raw HTML tags detected";

        var parts = new List<string>();
        parts.Add($"{totalTags} tag{(totalTags == 1 ? "" : "s")}");
        if (dangerousTags > 0) parts.Add($"\ud83d\udea8 {dangerousTags} dangerous tag{(dangerousTags == 1 ? "" : "s")}");
        if (eventHandlers > 0) parts.Add($"\ud83d\udea8 {eventHandlers} event handler{(eventHandlers == 1 ? "" : "s")}");
        if (unclosedTags > 0) parts.Add($"\u26a0 {unclosedTags} unclosed tag{(unclosedTags == 1 ? "" : "s")}");
        if (deprecatedTags > 0) parts.Add($"\u2139 {deprecatedTags} deprecated tag{(deprecatedTags == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown HTML sanitization audit ({details} · {category} · {score:0.#}% safety score)";
    }

    public static MarkdownYamlFrontmatterAudit AuditMarkdownYamlFrontmatterSchema(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatYamlFrontmatterSummary(false, 0, 0, 0, 0, 100.0, "Clean Baseline · No YAML Frontmatter");
            return new MarkdownYamlFrontmatterAudit(
                false, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownYamlFrontmatterItem>(),
                "Clean Baseline · No YAML Frontmatter", emptySummary);
        }

        var normalized = markdownText.TrimStart('\uFEFF');
        var lines = normalized.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        bool startsWithOpening = lines.Length > 0 && lines[0].Trim() == "---";
        if (!startsWithOpening)
        {
            int misplacedIdx = -1;
            for (int i = 1; i < Math.Min(lines.Length, 15); i++)
            {
                if (lines[i].Trim() == "---")
                {
                    misplacedIdx = i;
                    break;
                }
            }

            if (misplacedIdx != -1)
            {
                var itemsMisplaced = new List<MarkdownYamlFrontmatterItem>
                {
                    new MarkdownYamlFrontmatterItem(
                        misplacedIdx + 1,
                        "---",
                        "DelimiterError",
                        "Warning",
                        lines[misplacedIdx],
                        "YAML frontmatter delimiter '---' found after line 1; frontmatter blocks must start at line 1.")
                };
                string summaryMisplaced = FormatYamlFrontmatterSummary(false, 0, 1, 0, 0, 80.0, "Misplaced Frontmatter Delimiter");
                return new MarkdownYamlFrontmatterAudit(
                    false, 0, 1, 0, 0, 80.0,
                    itemsMisplaced, "Misplaced Frontmatter Delimiter", summaryMisplaced);
            }

            string cleanSummary = FormatYamlFrontmatterSummary(false, 0, 0, 0, 0, 100.0, "Clean Baseline · No YAML Frontmatter");
            return new MarkdownYamlFrontmatterAudit(
                false, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownYamlFrontmatterItem>(),
                "Clean Baseline · No YAML Frontmatter", cleanSummary);
        }

        int closingIdx = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed == "---" || trimmed == "...")
            {
                closingIdx = i;
                break;
            }
        }

        var items = new List<MarkdownYamlFrontmatterItem>();
        if (closingIdx == -1)
        {
            items.Add(new MarkdownYamlFrontmatterItem(
                1,
                "---",
                "DelimiterError",
                "Error",
                lines[0],
                "Unclosed YAML frontmatter block; missing closing '---' or '...' delimiter."));
            string unclosedSummary = FormatYamlFrontmatterSummary(true, 0, 1, 0, 0, 50.0, "Unclosed Frontmatter Block");
            return new MarkdownYamlFrontmatterAudit(
                true, 0, 1, 0, 0, 50.0,
                items, "Unclosed Frontmatter Block", unclosedSummary);
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int syntaxErrors = 0;
        int duplicateKeys = 0;
        int totalKeys = 0;

        for (int i = 1; i < closingIdx; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            if (trimmed.StartsWith('-'))
                continue;

            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                string keyPart = trimmed[..colonIdx].Trim();
                string valPart = trimmed[(colonIdx + 1)..].Trim();

                if (System.Text.RegularExpressions.Regex.IsMatch(keyPart, @"^[A-Za-z0-9_-]+$"))
                {
                    totalKeys++;
                    if (!seenKeys.Add(keyPart))
                    {
                        duplicateKeys++;
                        items.Add(new MarkdownYamlFrontmatterItem(
                            i + 1,
                            keyPart,
                            "DuplicateKey",
                            "Warning",
                            rawLine,
                            $"Duplicate YAML frontmatter key '{keyPart}'; duplicate keys can cause parser ambiguity or data overwrite."));
                    }

                    if (valPart.Contains(": ") && !valPart.StartsWith('"') && !valPart.StartsWith('\''))
                    {
                        items.Add(new MarkdownYamlFrontmatterItem(
                            i + 1,
                            keyPart,
                            "FormattingWarning",
                            "Warning",
                            rawLine,
                            $"Scalar value for key '{keyPart}' contains ': ' without enclosing quotes; enclose in quotes to avoid YAML parsing errors."));
                    }

                    if (keyPart.Equals("date", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(valPart))
                    {
                        var cleanDate = valPart.Trim('\'', '"');
                        if (!DateTime.TryParse(cleanDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                        {
                            items.Add(new MarkdownYamlFrontmatterItem(
                                i + 1,
                                keyPart,
                                "FormattingWarning",
                                "Warning",
                                rawLine,
                                $"Date value '{valPart}' could not be parsed as standard ISO-8601 or culture-invariant date."));
                        }
                    }
                }
                else
                {
                    syntaxErrors++;
                    items.Add(new MarkdownYamlFrontmatterItem(
                        i + 1,
                        keyPart,
                        "SyntaxError",
                        "Error",
                        rawLine,
                        $"Invalid key identifier '{keyPart}'; YAML keys should contain alphanumeric characters, hyphens, or underscores."));
                }
            }
            else
            {
                syntaxErrors++;
                items.Add(new MarkdownYamlFrontmatterItem(
                    i + 1,
                    trimmed,
                    "SyntaxError",
                    "Error",
                    rawLine,
                    $"Malformed YAML line; expected 'key: value' mapping, comment, or list entry."));
            }
        }

        int missingRecommended = 0;
        string[] recommended = new[] { "title" };
        foreach (var req in recommended)
        {
            if (!seenKeys.Contains(req))
            {
                missingRecommended++;
                items.Add(new MarkdownYamlFrontmatterItem(
                    1,
                    req,
                    "MissingKey",
                    "Info",
                    lines[0],
                    $"Frontmatter block is missing recommended metadata key '{req}'."));
            }
        }

        double score = 100.0;
        score -= syntaxErrors * 15.0;
        score -= duplicateKeys * 10.0;
        score -= missingRecommended * 5.0;
        score = Math.Clamp(score, 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 => "Strictly Valid Schema",
            >= 80.0 => "Valid with Minor Warnings",
            >= 60.0 => "Schema Inconsistencies",
            _ => "Malformed Frontmatter Schema"
        };

        string summary = FormatYamlFrontmatterSummary(true, totalKeys, syntaxErrors, duplicateKeys, missingRecommended, score, category);
        return new MarkdownYamlFrontmatterAudit(
            true, totalKeys, syntaxErrors, duplicateKeys, missingRecommended, score, items, category, summary);
    }

    public static string FormatYamlFrontmatterSummary(string? markdownText)
        => AuditMarkdownYamlFrontmatterSchema(markdownText).Summary;

    public static string FormatYamlFrontmatterSummary(
        bool hasFrontmatter, int totalKeys, int syntaxErrors, int duplicateKeys, int missingRecommendedKeys, double score, string category)
    {
        if (!hasFrontmatter)
            return "Markdown YAML frontmatter audit: Clean document · No YAML frontmatter block present";

        var parts = new List<string>();
        parts.Add($"{totalKeys} key{(totalKeys == 1 ? "" : "s")}");
        if (syntaxErrors > 0) parts.Add($"🚨 {syntaxErrors} syntax error{(syntaxErrors == 1 ? "" : "s")}");
        if (duplicateKeys > 0) parts.Add($"⚠ {duplicateKeys} duplicate key{(duplicateKeys == 1 ? "" : "s")}");
        if (missingRecommendedKeys > 0) parts.Add($"ℹ {missingRecommendedKeys} missing recommended");

        string details = string.Join(" · ", parts);
        return $"Markdown YAML frontmatter audit ({details} · {category} · {score:0.#}% schema score)";
    }


    public static MarkdownTableColumnAlignmentStrictnessAudit AuditMarkdownTableColumnAlignmentStrictness(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatTableColumnAlignmentStrictnessSummary(0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline · No Tables");
            return new MarkdownTableColumnAlignmentStrictnessAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTableColumnAlignmentStrictnessItem>(),
                "Clean Baseline · No Tables", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownTableColumnAlignmentStrictnessItem>();

        int totalTables = 0;
        int totalRows = 0;
        int alignedTables = 0;
        int columnMismatchCount = 0;
        int malformedSeparatorCount = 0;
        int missingOuterPipeCount = 0;

        bool inCodeBlock = false;
        var currentTableLines = new List<(int LineNumber, string Line)>();

        void ProcessTable()
        {
            if (currentTableLines.Count < 2)
            {
                currentTableLines.Clear();
                return;
            }

            int headerLineNum = currentTableLines[0].LineNumber;
            string headerLine = currentTableLines[0].Line;
            var headerCells = SplitTableCellsStrict(headerLine, out bool headerHasLeading, out bool headerHasTrailing);
            int expectedCols = headerCells.Count;

            if (expectedCols < 1)
            {
                currentTableLines.Clear();
                return;
            }

            int sepLineNum = currentTableLines[1].LineNumber;
            string sepLine = currentTableLines[1].Line;
            var sepCells = SplitTableCellsStrict(sepLine, out bool sepHasLeading, out bool sepHasTrailing);

            bool isMalformedSep = false;
            if (sepCells.Count != expectedCols)
            {
                isMalformedSep = true;
            }
            foreach (var cell in sepCells)
            {
                var trimmed = cell.Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^:?-+:?$") || !trimmed.Contains('-'))
                {
                    isMalformedSep = true;
                    break;
                }
            }

            if (isMalformedSep && sepCells.Count < 2)
            {
                currentTableLines.Clear();
                return;
            }

            totalTables++;
            int tableIndex = totalTables;
            bool tableHasIssues = false;

            bool headerMissingOuter = !headerHasLeading || !headerHasTrailing;
            if (headerMissingOuter) missingOuterPipeCount++;
            items.Add(new MarkdownTableColumnAlignmentStrictnessItem(
                headerLineNum, tableIndex, "Header", expectedCols, expectedCols,
                headerMissingOuter, false, false, headerLine,
                headerMissingOuter ? "Header row lacks enclosing outer pipes '|'." : "Valid header row."));
            totalRows++;

            bool sepMissingOuter = !sepHasLeading || !sepHasTrailing;
            if (sepMissingOuter) missingOuterPipeCount++;
            if (isMalformedSep)
            {
                malformedSeparatorCount++;
                tableHasIssues = true;
            }
            items.Add(new MarkdownTableColumnAlignmentStrictnessItem(
                sepLineNum, tableIndex, "Separator", sepCells.Count, expectedCols,
                sepMissingOuter, isMalformedSep, sepCells.Count != expectedCols, sepLine,
                isMalformedSep ? "Delimiter separator row contains invalid characters or column count mismatch." : "Valid delimiter separator row."));
            totalRows++;

            for (int r = 2; r < currentTableLines.Count; r++)
            {
                int rowLineNum = currentTableLines[r].LineNumber;
                string rowLine = currentTableLines[r].Line;
                var cells = SplitTableCellsStrict(rowLine, out bool rowHasLeading, out bool rowHasTrailing);
                totalRows++;

                bool rowMissingOuter = !rowHasLeading || !rowHasTrailing;
                if (rowMissingOuter) missingOuterPipeCount++;

                bool rowColMismatch = cells.Count != expectedCols;
                if (rowColMismatch)
                {
                    columnMismatchCount++;
                    tableHasIssues = true;
                }

                items.Add(new MarkdownTableColumnAlignmentStrictnessItem(
                    rowLineNum, tableIndex, "DataRow", cells.Count, expectedCols,
                    rowMissingOuter, false, rowColMismatch, rowLine,
                    rowColMismatch
                        ? $"Row has {cells.Count} columns but header defined {expectedCols} columns."
                        : (rowMissingOuter ? "Data row lacks enclosing outer pipes '|'." : "Aligned data row.")));
            }

            if (!tableHasIssues) alignedTables++;
            currentTableLines.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                if (currentTableLines.Count > 0) ProcessTable();
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock) continue;

            if (trimmed.Contains('|') && !trimmed.StartsWith(">"))
            {
                currentTableLines.Add((i + 1, raw));
            }
            else
            {
                if (currentTableLines.Count > 0)
                {
                    ProcessTable();
                }
            }
        }

        if (currentTableLines.Count > 0)
        {
            ProcessTable();
        }

        if (totalTables == 0)
        {
            string cleanSummary = FormatTableColumnAlignmentStrictnessSummary(0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline · No Tables");
            return new MarkdownTableColumnAlignmentStrictnessAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTableColumnAlignmentStrictnessItem>(),
                "Clean Baseline · No Tables", cleanSummary);
        }

        double score = 100.0;
        score -= malformedSeparatorCount * 20.0;
        score -= columnMismatchCount * 10.0;
        score -= Math.Min(missingOuterPipeCount * 2.0, 15.0);
        score = Math.Clamp(score, 0.0, 100.0);

        string category = score switch
        {
            >= 95.0 => "Strictly Aligned Tables",
            >= 80.0 => "High Table Fidelity",
            >= 60.0 => "Minor Column Misalignments",
            _ => "Malformed Table Syntax"
        };

        string summary = FormatTableColumnAlignmentStrictnessSummary(
            totalTables, totalRows, alignedTables, columnMismatchCount, malformedSeparatorCount, missingOuterPipeCount, score, category);

        return new MarkdownTableColumnAlignmentStrictnessAudit(
            totalTables, totalRows, alignedTables, columnMismatchCount, malformedSeparatorCount, missingOuterPipeCount, score, items, category, summary);
    }

    private static List<string> SplitTableCellsStrict(string line, out bool hasLeadingPipe, out bool hasTrailingPipe)
    {
        var cells = new List<string>();
        string trimmed = line.Trim();
        hasLeadingPipe = trimmed.StartsWith('|');
        hasTrailingPipe = trimmed.EndsWith('|') && trimmed.Length > 1 && (trimmed.Length < 2 || trimmed[^2] != '\\');

        if (hasLeadingPipe) trimmed = trimmed[1..];
        if (hasTrailingPipe && trimmed.Length > 0) trimmed = trimmed[..^1];

        var cur = new System.Text.StringBuilder();
        bool inBacktick = false;

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '`' && (i == 0 || trimmed[i - 1] != '\\'))
            {
                inBacktick = !inBacktick;
                cur.Append(c);
            }
            else if (c == '|' && !inBacktick && (i == 0 || trimmed[i - 1] != '\\'))
            {
                cells.Add(cur.ToString().Trim());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }
        cells.Add(cur.ToString().Trim());
        return cells;
    }

    public static string FormatTableColumnAlignmentStrictnessSummary(string? markdownText)
        => AuditMarkdownTableColumnAlignmentStrictness(markdownText).Summary;

    public static string FormatTableColumnAlignmentStrictnessSummary(
        int totalTables, int totalRows, int alignedTables, int columnMismatches, int malformedSeparators, int missingOuterPipes, double score, string category)
    {
        if (totalTables == 0)
            return "Markdown table column alignment audit: Clean document · No markdown tables present";

        var parts = new List<string>();
        parts.Add($"{totalTables} table{(totalTables == 1 ? "" : "s")} ({totalRows} row{(totalRows == 1 ? "" : "s")})");
        parts.Add($"{alignedTables} aligned");
        if (columnMismatches > 0) parts.Add($"🚨 {columnMismatches} column mismatch{(columnMismatches == 1 ? "" : "es")}");
        if (malformedSeparators > 0) parts.Add($"⚠ {malformedSeparators} malformed separator{(malformedSeparators == 1 ? "" : "s")}");
        if (missingOuterPipes > 0) parts.Add($"ℹ {missingOuterPipes} missing outer pipe{(missingOuterPipes == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown table column alignment audit ({details} · {category} · {score:0.#}% fidelity score)";
    }
    public static MarkdownFootnoteSequentialNumberingAudit AuditMarkdownFootnoteSequentialNumbering(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatFootnoteSequentialNumberingSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline · No Footnotes");
            return new MarkdownFootnoteSequentialNumberingAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownFootnoteSequentialNumberingItem>(),
                "Clean Baseline · No Footnotes", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownFootnoteSequentialNumberingItem>();

        var defs = new Dictionary<string, List<(int LineNumber, string Excerpt)>>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<(string Label, int LineNumber, string Excerpt)>();
        var defLabelsInOrder = new List<string>();
        var refLabelsInOrder = new List<string>();

        bool inCodeBlock = false;
        var defRegex = new System.Text.RegularExpressions.Regex(@"^\s{0,3}\[\^([^\]]+)\]:\s*(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
        var refRegex = new System.Text.RegularExpressions.Regex(@"\[\^([^\]]+)\](?!\:)", System.Text.RegularExpressions.RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock) continue;

            var defMatch = defRegex.Match(line);
            if (defMatch.Success)
            {
                string label = defMatch.Groups[1].Value.Trim();
                string excerpt = line.Length > 80 ? line.Substring(0, 77) + "..." : line;

                if (!defs.TryGetValue(label, out var list))
                {
                    list = new List<(int, string)>();
                    defs[label] = list;
                    defLabelsInOrder.Add(label);
                }
                list.Add((lineNum, excerpt));
            }
            else
            {
                var matches = refRegex.Matches(line);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    string label = match.Groups[1].Value.Trim();
                    string excerpt = line.Length > 80 ? line.Substring(0, 77) + "..." : line;
                    refs.Add((label, lineNum, excerpt));
                    refLabelsInOrder.Add(label);
                }
            }
        }

        int totalRefs = refs.Count;
        int totalDefs = defs.Values.Sum(list => list.Count);

        if (totalRefs == 0 && totalDefs == 0)
        {
            string emptySummary = FormatFootnoteSequentialNumberingSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline · No Footnotes");
            return new MarkdownFootnoteSequentialNumberingAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownFootnoteSequentialNumberingItem>(),
                "Clean Baseline · No Footnotes", emptySummary);
        }

        int orphanRefCount = 0;
        int unreferencedDefCount = 0;
        int duplicateDefCount = 0;
        int disorderedSequenceCount = 0;
        int sequentialNumericCount = 0;

        foreach (var kvp in defs)
        {
            if (kvp.Value.Count > 1)
            {
                duplicateDefCount += kvp.Value.Count - 1;
                foreach (var occ in kvp.Value.Skip(1))
                {
                    items.Add(new MarkdownFootnoteSequentialNumberingItem(
                        kvp.Key, occ.LineNumber, "DuplicateDefinition", "Warning", occ.Excerpt,
                        $"Footnote definition '[^{kvp.Key}]' is declared multiple times. Remove redundant duplicate declarations."));
                }
            }
        }

        foreach (var r in refs)
        {
            if (!defs.ContainsKey(r.Label))
            {
                orphanRefCount++;
                items.Add(new MarkdownFootnoteSequentialNumberingItem(
                    r.Label, r.LineNumber, "OrphanReference", "Hazard", r.Excerpt,
                    $"Footnote reference '[^{r.Label}]' has no matching definition '[^{r.Label}]: ...'. Add the missing footnote definition."));
            }
        }

        var referencedLabelSet = new HashSet<string>(refs.Select(r => r.Label), StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in defs)
        {
            if (!referencedLabelSet.Contains(kvp.Key))
            {
                unreferencedDefCount += kvp.Value.Count;
                foreach (var occ in kvp.Value)
                {
                    items.Add(new MarkdownFootnoteSequentialNumberingItem(
                        kvp.Key, occ.LineNumber, "UnreferencedDefinition", "Warning", occ.Excerpt,
                        $"Footnote definition '[^{kvp.Key}]: ...' is never referenced in document body. Remove orphan definition or link it."));
                }
            }
        }

        int lastNumeric = 0;
        foreach (var r in refs)
        {
            if (int.TryParse(r.Label, out int currentNum))
            {
                if (lastNumeric == 0)
                {
                    if (currentNum == 1)
                    {
                        sequentialNumericCount++;
                    }
                    else
                    {
                        disorderedSequenceCount++;
                        items.Add(new MarkdownFootnoteSequentialNumberingItem(
                            r.Label, r.LineNumber, "DisorderedSequence", "Caution", r.Excerpt,
                            $"First numeric footnote is '[^{currentNum}]' instead of '[^1]'. Start numeric footnotes from 1."));
                    }
                }
                else if (currentNum == lastNumeric + 1)
                {
                    sequentialNumericCount++;
                }
                else if (currentNum <= lastNumeric)
                {
                    disorderedSequenceCount++;
                    items.Add(new MarkdownFootnoteSequentialNumberingItem(
                        r.Label, r.LineNumber, "SequenceReversal", "Hazard", r.Excerpt,
                        $"Numeric footnote '[^{currentNum}]' appears after '[^{lastNumeric}]'. Footnote references should follow ascending order."));
                }
                else
                {
                    disorderedSequenceCount++;
                    items.Add(new MarkdownFootnoteSequentialNumberingItem(
                        r.Label, r.LineNumber, "SequenceGap", "Caution", r.Excerpt,
                        $"Numeric footnote jumps from '[^{lastNumeric}]' to '[^{currentNum}]', skipping intervening numbers. Renumber monotonically."));
                }
                lastNumeric = currentNum;
            }
            else
            {
                if (r.Label.Any(c => char.IsWhiteSpace(c) || c == ':' || c == '^' || c == '[' || c == ']'))
                {
                    items.Add(new MarkdownFootnoteSequentialNumberingItem(
                        r.Label, r.LineNumber, "MalformedIdentifier", "Hazard", r.Excerpt,
                        $"Footnote identifier '[^{r.Label}]' contains invalid characters. Use alphanumeric characters, hyphens, or underscores."));
                }
            }
        }

        double penalty = (orphanRefCount * 25.0) +
                         (unreferencedDefCount * 15.0) +
                         (duplicateDefCount * 15.0) +
                         (disorderedSequenceCount * 10.0);
        double score = Math.Max(0.0, Math.Min(100.0, 100.0 - penalty));

        string category = score switch
        {
            >= 95.0 => "Optimal · Perfect Footnote Hygiene",
            >= 80.0 => "Good · Minor Footnote Warnings",
            >= 60.0 => "Caution · Footnote Reference Anomalies",
            _ => "Hazardous · Severely Desynchronized Footnotes"
        };

        string summary = FormatFootnoteSequentialNumberingSummary(
            totalRefs, totalDefs, sequentialNumericCount, orphanRefCount, unreferencedDefCount,
            duplicateDefCount, disorderedSequenceCount, score, category);

        return new MarkdownFootnoteSequentialNumberingAudit(
            totalRefs, totalDefs, sequentialNumericCount, orphanRefCount, unreferencedDefCount,
            duplicateDefCount, disorderedSequenceCount, score, items, category, summary);
    }

    public static string FormatFootnoteSequentialNumberingSummary(string? markdownText)
        => AuditMarkdownFootnoteSequentialNumbering(markdownText).Summary;

    public static string FormatFootnoteSequentialNumberingSummary(
        int totalRefs, int totalDefs, int sequentialCount, int orphanRefs, int unreferencedDefs,
        int duplicateDefs, int disorderedSeq, double score, string category)
    {
        if (totalRefs == 0 && totalDefs == 0)
            return "Markdown footnote sequential numbering audit: Clean document · No footnotes present";

        var parts = new List<string>();
        parts.Add($"{totalRefs} ref{(totalRefs == 1 ? "" : "s")}");
        parts.Add($"{totalDefs} def{(totalDefs == 1 ? "" : "s")}");
        if (sequentialCount > 0) parts.Add($"✓ {sequentialCount} sequential");
        if (orphanRefs > 0) parts.Add($"🚨 {orphanRefs} orphan ref{(orphanRefs == 1 ? "" : "s")}");
        if (unreferencedDefs > 0) parts.Add($"⚠ {unreferencedDefs} unreferenced def{(unreferencedDefs == 1 ? "" : "s")}");
        if (duplicateDefs > 0) parts.Add($"🚨 {duplicateDefs} duplicate def{(duplicateDefs == 1 ? "" : "s")}");
        if (disorderedSeq > 0) parts.Add($"⚡ {disorderedSeq} disordered/gap{(disorderedSeq == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown footnote sequential numbering audit ({details} · {category} · {score:0.#}% integrity score)";
    }


    public static MarkdownBlockquoteAlertCalloutsAudit AuditMarkdownBlockquoteAlertCallouts(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatBlockquoteAlertCalloutsSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline · No Alert Callouts");
            return new MarkdownBlockquoteAlertCalloutsAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownBlockquoteAlertCalloutItem>(),
                "Clean Baseline · No Alert Callouts", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownBlockquoteAlertCalloutItem>();

        int totalCallouts = 0;
        int validCanonical = 0;
        int validExtended = 0;
        int missingExclamation = 0;
        int unrecognizedTag = 0;
        int malformedSyntax = 0;
        int emptyCallouts = 0;

        var canonicalTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NOTE", "TIP", "IMPORTANT", "WARNING", "CAUTION"
        };

        var extendedKnownTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "INFO", "HINT", "SUCCESS", "CHECK", "QUESTION", "FAQ", "HELP", "ATTENTION",
            "FAILURE", "FAIL", "DANGER", "ERROR", "BUG", "EXAMPLE", "QUOTE", "CITE", "SUMMARY", "ABSTRACT", "TLDR"
        };

        bool inCodeBlock = false;
        var quoteStartRegex = new System.Text.RegularExpressions.Regex(@"^\s{0,3}>\s*(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
        var calloutTagRegex = new System.Text.RegularExpressions.Regex(@"^\[!([A-Za-z0-9_-]+)\](?:\s*(.*))?$", System.Text.RegularExpressions.RegexOptions.Compiled);
        var missingBangRegex = new System.Text.RegularExpressions.Regex(@"^\[([A-Za-z0-9_-]+)\](?!\()(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock) continue;

            var qMatch = quoteStartRegex.Match(line);
            if (!qMatch.Success) continue;

            string quoteContent = qMatch.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(quoteContent)) continue;

            var tagMatch = calloutTagRegex.Match(quoteContent);
            if (tagMatch.Success)
            {
                totalCallouts++;
                string tag = tagMatch.Groups[1].Value;
                string title = tagMatch.Groups[2].Success ? tagMatch.Groups[2].Value.Trim() : string.Empty;

                bool isCanonical = canonicalTags.Contains(tag);
                bool isExtended = extendedKnownTags.Contains(tag);

                bool hasBody = !string.IsNullOrEmpty(title);
                if (!hasBody && i + 1 < lines.Length)
                {
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        string nextLine = lines[j].Trim();
                        if (string.IsNullOrWhiteSpace(nextLine)) break;
                        var nextQ = quoteStartRegex.Match(lines[j]);
                        if (!nextQ.Success) break;
                        if (!string.IsNullOrWhiteSpace(nextQ.Groups[1].Value))
                        {
                            hasBody = true;
                            break;
                        }
                    }
                }

                if (!hasBody)
                {
                    emptyCallouts++;
                    items.Add(new MarkdownBlockquoteAlertCalloutItem(
                        lineNum, $"[!{tag}]", tag, "EmptyCalloutBody", "Warning",
                        line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                        $"Alert callout '[!{tag}]' has no body content or title. Provide contextual text inside the callout."));
                }

                if (isCanonical)
                {
                    validCanonical++;
                    items.Add(new MarkdownBlockquoteAlertCalloutItem(
                        lineNum, $"[!{tag}]", tag.ToUpperInvariant(), "CanonicalCallout", "Valid",
                        line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                        $"Standard GFM alert callout '[!{tag.ToUpperInvariant()}]' properly formatted."));
                }
                else if (isExtended)
                {
                    validExtended++;
                    items.Add(new MarkdownBlockquoteAlertCalloutItem(
                        lineNum, $"[!{tag}]", tag, "ExtendedCallout", "Info",
                        line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                        $"Extended callout type '[!{tag}]' detected. Compatible with Obsidian/extended renderers."));
                }
                else
                {
                    unrecognizedTag++;
                    items.Add(new MarkdownBlockquoteAlertCalloutItem(
                        lineNum, $"[!{tag}]", tag, "UnrecognizedCalloutTag", "Hazard",
                        line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                        $"Unrecognized alert callout '[!{tag}]'. Use standard types: NOTE, TIP, IMPORTANT, WARNING, CAUTION."));
                }
            }
            else
            {
                var noBangMatch = missingBangRegex.Match(quoteContent);
                if (noBangMatch.Success)
                {
                    string candidate = noBangMatch.Groups[1].Value;
                    if (canonicalTags.Contains(candidate) || extendedKnownTags.Contains(candidate))
                    {
                        totalCallouts++;
                        missingExclamation++;
                        items.Add(new MarkdownBlockquoteAlertCalloutItem(
                            lineNum, $"[{candidate}]", candidate, "MissingExclamationPrefix", "Hazard",
                            line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                            $"Blockquote starts with '[{candidate}]' missing the '!' prefix. Use '[!{candidate.ToUpperInvariant()}]' for alert callouts."));
                    }
                }
                else if (quoteContent.StartsWith("[!", StringComparison.Ordinal) && !quoteContent.Contains("]"))
                {
                    totalCallouts++;
                    malformedSyntax++;
                    items.Add(new MarkdownBlockquoteAlertCalloutItem(
                        lineNum, quoteContent, "Unknown", "UnclosedAlertTag", "Hazard",
                        line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                        "Unclosed alert callout tag '[!...'. Ensure closing bracket ']' is present."));
                }
            }
        }

        if (totalCallouts == 0)
        {
            string emptySummary = FormatBlockquoteAlertCalloutsSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline · No Alert Callouts");
            return new MarkdownBlockquoteAlertCalloutsAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownBlockquoteAlertCalloutItem>(),
                "Clean Baseline · No Alert Callouts", emptySummary);
        }

        double penalty = (missingExclamation * 25.0) + (malformedSyntax * 20.0) + (unrecognizedTag * 15.0) + (emptyCallouts * 10.0);
        double score = Math.Max(0.0, Math.Min(100.0, 100.0 - penalty));

        string category = score switch
        {
            >= 95.0 => "Optimal · Perfect Callout Grammar",
            >= 80.0 => "Good · Minor Callout Warnings",
            >= 60.0 => "Caution · Callout Syntax Errors",
            _ => "Hazardous · Malformed Alert Callouts"
        };

        string summary = FormatBlockquoteAlertCalloutsSummary(
            totalCallouts, validCanonical, validExtended, missingExclamation, unrecognizedTag, malformedSyntax, emptyCallouts, score, category);

        return new MarkdownBlockquoteAlertCalloutsAudit(
            totalCallouts, validCanonical, validExtended, missingExclamation, unrecognizedTag, malformedSyntax, emptyCallouts,
            score, items, category, summary);
    }

    public static string FormatBlockquoteAlertCalloutsSummary(string? markdownText)
        => AuditMarkdownBlockquoteAlertCallouts(markdownText).Summary;

    public static string FormatBlockquoteAlertCalloutsSummary(
        int totalCallouts, int canonical, int extended, int missingBang, int unrecognized, int malformed, int empty, double score, string category)
    {
        if (totalCallouts == 0)
            return "Markdown alert callout audit: Clean document · No alert callouts present";

        var parts = new List<string>();
        parts.Add($"{totalCallouts} callout{(totalCallouts == 1 ? "" : "s")}");
        if (canonical > 0) parts.Add($"✓ {canonical} canonical");
        if (extended > 0) parts.Add($"ℹ {extended} extended");
        if (missingBang > 0) parts.Add($"🚨 {missingBang} missing '!' prefix");
        if (unrecognized > 0) parts.Add($"⚠ {unrecognized} unrecognized tag{(unrecognized == 1 ? "" : "s")}");
        if (malformed > 0) parts.Add($"🚨 {malformed} malformed syntax");
        if (empty > 0) parts.Add($"⚡ {empty} empty body");

        string details = string.Join(" · ", parts);
        return $"Markdown alert callout audit ({details} · {category} · {score:0.#}% grammar score)";
    }


    public static MarkdownTaskListPrioritiesAudit AuditMarkdownTaskListPriorities(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatMarkdownTaskListPrioritiesSummary(0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline · No Tasks");
            return new MarkdownTaskListPrioritiesAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTaskListPriorityItem>(),
                "Clean Baseline · No Tasks", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownTaskListPriorityItem>();

        int totalTasks = 0;
        int validPriorities = 0;
        int validDeadlines = 0;
        int malformedPriorities = 0;
        int malformedDeadlines = 0;
        int untaggedTasks = 0;

        var validPriorityKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "A", "B", "C", "D", "HIGH", "MEDIUM", "LOW", "URGENT", "CRITICAL", "NORMAL", "P0", "P1", "P2", "P3", "P4"
        };

        bool inCodeBlock = false;
        var taskRegex = new System.Text.RegularExpressions.Regex(@"^\s*(?:[-*+]|\d+\.)\s+\[([ xX])\]\s*(.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);
        var priorityTagRegex = new System.Text.RegularExpressions.Regex(@"\[#([A-Za-z0-9_-]+)\](?![\(\[])", System.Text.RegularExpressions.RegexOptions.Compiled);
        var deadlineRegex = new System.Text.RegularExpressions.Regex(@"(?:@due|@deadline|due:)\s*(?:\(\s*([^)]*?)\s*\)|([0-9\-\/]{8,10}))", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", System.StringComparison.Ordinal) || trimmed.StartsWith("~~~", System.StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock) continue;

            var tMatch = taskRegex.Match(line);
            if (!tMatch.Success) continue;

            totalTasks++;
            string taskContent = tMatch.Groups[2].Value.Trim();

            bool hasPriority = false;
            bool hasDeadline = false;

            // Check priority tags
            var pMatches = priorityTagRegex.Matches(taskContent);
            if (pMatches.Count > 0)
            {
                foreach (System.Text.RegularExpressions.Match pm in pMatches)
                {
                    string tag = pm.Groups[1].Value;
                    if (validPriorityKeywords.Contains(tag))
                    {
                        validPriorities++;
                        hasPriority = true;
                        items.Add(new MarkdownTaskListPriorityItem(
                            lineNum, pm.Value, tag.ToUpperInvariant(), "", "ValidPriorityTag", "Valid",
                            line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                            $"Valid task priority marker '{pm.Value}'."));
                    }
                    else
                    {
                        malformedPriorities++;
                        hasPriority = true;
                        items.Add(new MarkdownTaskListPriorityItem(
                            lineNum, pm.Value, tag, "", "UnrecognizedPriorityTag", "Warning",
                            line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                            $"Unrecognized priority tag '{pm.Value}'. Use canonical priorities like [#A], [#B], [#high], [#medium], [#low], or [#p1]."));
                    }
                }
            }
            else if (taskContent.Contains("[#") && !taskContent.Contains("]"))
            {
                malformedPriorities++;
                hasPriority = true;
                items.Add(new MarkdownTaskListPriorityItem(
                    lineNum, "[#...", "Malformed", "", "UnclosedPriorityBracket", "Hazard",
                    line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                    "Unclosed priority tag '[#...'. Close with ']' bracket."));
            }

            // Check deadline tags
            var dMatches = deadlineRegex.Matches(taskContent);
            if (dMatches.Count > 0)
            {
                foreach (System.Text.RegularExpressions.Match dm in dMatches)
                {
                    string rawDate = dm.Groups[1].Success && !string.IsNullOrWhiteSpace(dm.Groups[1].Value)
                        ? dm.Groups[1].Value.Trim()
                        : dm.Groups[2].Value.Trim();

                    bool validDate = false;
                    string[] formats = new[] { "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-M-d", "yyyy/M/d" };
                    if (System.DateTime.TryParseExact(rawDate, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                    {
                        validDate = true;
                    }

                    if (validDate)
                    {
                        validDeadlines++;
                        hasDeadline = true;
                        items.Add(new MarkdownTaskListPriorityItem(
                            lineNum, dm.Value, "", rawDate, "ValidDeadlineDate", "Valid",
                            line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                            $"Valid ISO-8601 deadline tag '{dm.Value}'."));
                    }
                    else
                    {
                        malformedDeadlines++;
                        hasDeadline = true;
                        items.Add(new MarkdownTaskListPriorityItem(
                            lineNum, dm.Value, "", rawDate, "MalformedDeadlineDate", "Hazard",
                            line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                            $"Invalid deadline date '{rawDate}' in tag '{dm.Value}'. Use valid ISO format '@due(YYYY-MM-DD)'."));
                    }
                }
            }
            else if (taskContent.Contains("@due(") && !taskContent.Contains(")"))
            {
                malformedDeadlines++;
                hasDeadline = true;
                items.Add(new MarkdownTaskListPriorityItem(
                    lineNum, "@due(...", "", "Unclosed", "UnclosedDeadlineParenthesis", "Hazard",
                    line.Length > 80 ? line.Substring(0, 77) + "..." : line,
                    "Unclosed deadline tag '@due(...'. Ensure closing parenthesis ')' is present."));
            }

            if (!hasPriority && !hasDeadline)
            {
                untaggedTasks++;
            }
        }

        if (totalTasks == 0)
        {
            string emptySummary = FormatMarkdownTaskListPrioritiesSummary(0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline · No Tasks");
            return new MarkdownTaskListPrioritiesAudit(
                0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownTaskListPriorityItem>(),
                "Clean Baseline · No Tasks", emptySummary);
        }

        double penalty = (malformedPriorities * 25.0) + (malformedDeadlines * 20.0);
        double score = Math.Max(0.0, Math.Min(100.0, 100.0 - penalty));

        string category = score switch
        {
            >= 95.0 => "Optimal · Standardized Task Tags",
            >= 80.0 => "Good · Minor Task Metadata Warnings",
            >= 60.0 => "Caution · Malformed Priorities & Deadlines",
            _ => "Hazardous · Severe Task Syntax Errors"
        };

        string summary = FormatMarkdownTaskListPrioritiesSummary(
            totalTasks, validPriorities, validDeadlines, malformedPriorities, malformedDeadlines, untaggedTasks, score, category);

        return new MarkdownTaskListPrioritiesAudit(
            totalTasks, validPriorities, validDeadlines, malformedPriorities, malformedDeadlines, untaggedTasks,
            score, items, category, summary);
    }

    public static string FormatMarkdownTaskListPrioritiesSummary(string? markdownText)
        => AuditMarkdownTaskListPriorities(markdownText).Summary;

    public static string FormatMarkdownTaskListPrioritiesSummary(
        int totalTasks, int validPriorities, int validDeadlines, int malformedPriorities, int malformedDeadlines, int untagged, double score, string category)
    {
        if (totalTasks == 0)
            return "Markdown task list priority audit: Clean document · No tasks present";

        var parts = new List<string>();
        parts.Add($"{totalTasks} task{(totalTasks == 1 ? "" : "s")}");
        if (validPriorities > 0) parts.Add($"✓ {validPriorities} prioritized");
        if (validDeadlines > 0) parts.Add($"⏱ {validDeadlines} deadline{(validDeadlines == 1 ? "" : "s")}");
        if (malformedPriorities > 0) parts.Add($"🚨 {malformedPriorities} invalid priority");
        if (malformedDeadlines > 0) parts.Add($"🚨 {malformedDeadlines} malformed date");
        if (untagged > 0) parts.Add($"ℹ {untagged} untagged");

        string details = string.Join(" · ", parts);
        return $"Markdown task list priority audit ({details} · {category} · {score:0.#}% compliance score)";
    }

    public static MarkdownFormFieldsAudit AuditMarkdownFormFields(string? markdownText)
    {
        if (string.IsNullOrWhiteSpace(markdownText))
        {
            string emptySummary = FormatMarkdownFormFieldsSummary(0, 0, 0, 0, 0, 0, 0, 100.0, "Clean Baseline · No Form Controls");
            return new MarkdownFormFieldsAudit(
                0, 0, 0, 0, 0, 0, 0, 100.0,
                Array.Empty<MarkdownFormFieldItem>(),
                "Clean Baseline · No Form Controls", emptySummary);
        }

        var lines = markdownText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var items = new List<MarkdownFormFieldItem>();

        int totalFields = 0;
        int validDropdowns = 0;
        int validDates = 0;
        int validTexts = 0;
        int singleOptionDropdowns = 0;
        int malformedDates = 0;
        int malformedPlaceholders = 0;

        bool inCodeBlock = false;

        var dropdownRegex = new System.Text.RegularExpressions.Regex(@"\[dropdown:\s*(?<options>[^\]]+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        var dateRegex = new System.Text.RegularExpressions.Regex(@"\[date(?::\s*(?<date>[^\]]+))?\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        var textRegex = new System.Text.RegularExpressions.Regex(@"\[text(?::\s*(?:[""“](?<ph>(?:[^""”\\]|\\.)*?)[""”]|(?<ph>[^\]]*)))?\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        var unclosedRegex = new System.Text.RegularExpressions.Regex(@"\[(?:dropdown|date|text):[^\n\]]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNum = i + 1;
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock) continue;

            // Check unclosed tags on line
            var unclosedMatches = unclosedRegex.Matches(line);
            foreach (System.Text.RegularExpressions.Match um in unclosedMatches)
            {
                totalFields++;
                malformedPlaceholders++;
                items.Add(new MarkdownFormFieldItem(
                    lineNum,
                    um.Value,
                    "Unclosed",
                    "UnclosedFormFieldTag",
                    "High",
                    um.Value,
                    "Close form control tag with trailing ']' bracket."));
            }

            // Dropdown controls
            var ddMatches = dropdownRegex.Matches(line);
            foreach (System.Text.RegularExpressions.Match dm in ddMatches)
            {
                totalFields++;
                string optionsRaw = dm.Groups["options"].Value;
                var rawParts = optionsRaw.Split('|');
                var trimmedParts = rawParts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

                bool hasEmptyOption = rawParts.Any(p => string.IsNullOrWhiteSpace(p));

                if (trimmedParts.Count < 2)
                {
                    singleOptionDropdowns++;
                    items.Add(new MarkdownFormFieldItem(
                        lineNum,
                        dm.Value,
                        "Dropdown",
                        "SingleOptionDropdown",
                        "High",
                        dm.Value,
                        "Dropdown must provide at least two distinct options separated by '|'."));
                }
                else if (hasEmptyOption)
                {
                    singleOptionDropdowns++;
                    items.Add(new MarkdownFormFieldItem(
                        lineNum,
                        dm.Value,
                        "Dropdown",
                        "EmptyOptionInDropdown",
                        "Medium",
                        dm.Value,
                        "Remove consecutive or trailing empty option pipes ('||')."));
                }
                else
                {
                    validDropdowns++;
                }
            }

            // Date controls
            var dtMatches = dateRegex.Matches(line);
            foreach (System.Text.RegularExpressions.Match dtm in dtMatches)
            {
                totalFields++;
                string dateVal = dtm.Groups["date"].Value.Trim();
                if (string.IsNullOrEmpty(dateVal) || string.Equals(dateVal, "today", StringComparison.OrdinalIgnoreCase) || string.Equals(dateVal, "now", StringComparison.OrdinalIgnoreCase))
                {
                    validDates++;
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(dateVal, @"^\d{4}-\d{2}-\d{2}$"))
                {
                    if (DateTime.TryParseExact(dateVal, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                    {
                        validDates++;
                    }
                    else
                    {
                        malformedDates++;
                        items.Add(new MarkdownFormFieldItem(
                            lineNum,
                            dtm.Value,
                            "Date",
                            "InvalidDateCalendarValue",
                            "High",
                            dtm.Value,
                            "Ensure date represents a valid calendar day in YYYY-MM-DD format."));
                    }
                }
                else
                {
                    malformedDates++;
                    items.Add(new MarkdownFormFieldItem(
                        lineNum,
                        dtm.Value,
                        "Date",
                        "MalformedDateFormat",
                        "Medium",
                        dtm.Value,
                        "Format date using ISO-8601 (YYYY-MM-DD) or keyword 'today'."));
                }
            }

            // Text controls
            var txMatches = textRegex.Matches(line);
            foreach (System.Text.RegularExpressions.Match txm in txMatches)
            {
                totalFields++;
                string phVal = txm.Groups["ph"].Value;
                if (txm.Value.StartsWith("[text:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(phVal))
                {
                    malformedPlaceholders++;
                    items.Add(new MarkdownFormFieldItem(
                        lineNum,
                        txm.Value,
                        "Text",
                        "EmptyTextPlaceholder",
                        "Low",
                        txm.Value,
                        "Supply a non-empty placeholder string or use bare '[text]'."));
                }
                else
                {
                    validTexts++;
                }
            }
        }

        double penalty = (singleOptionDropdowns * 25.0) + (malformedDates * 20.0) + (malformedPlaceholders * 15.0);
        double score = totalFields == 0 ? 100.0 : Math.Clamp(100.0 - penalty, 0.0, 100.0);

        string category;
        if (totalFields == 0) category = "Clean Baseline · No Form Controls";
        else if (score >= 95.0) category = "Optimal Form Controls (95-100%)";
        else if (score >= 80.0) category = "Good Form Controls with Minor Formatting Warnings (80-94%)";
        else if (score >= 60.0) category = "Acceptable Form Controls with Noticeable Syntax Defects (60-79%)";
        else category = "Poor Form Control Syntax (<60%)";

        string summary = FormatMarkdownFormFieldsSummary(
            totalFields, validDropdowns, validDates, validTexts, singleOptionDropdowns, malformedDates, malformedPlaceholders, score, category);

        return new MarkdownFormFieldsAudit(
            totalFields, validDropdowns, validDates, validTexts, singleOptionDropdowns, malformedDates, malformedPlaceholders,
            score, items, category, summary);
    }

    public static string FormatMarkdownFormFieldsSummary(
        int totalFields, int validDropdowns, int validDates, int validTexts, int singleOptionDropdowns, int malformedDates, int malformedPlaceholders, double score, string category)
    {
        if (totalFields == 0)
            return "Markdown form field audit: Clean document · No form controls present";

        var parts = new List<string>();
        parts.Add($"{totalFields} field{(totalFields == 1 ? "" : "s")}");
        if (validDropdowns > 0) parts.Add($"✓ {validDropdowns} dropdown{(validDropdowns == 1 ? "" : "s")}");
        if (validDates > 0) parts.Add($"✓ {validDates} date{(validDates == 1 ? "" : "s")}");
        if (validTexts > 0) parts.Add($"✓ {validTexts} text{(validTexts == 1 ? "" : "s")}");
        if (singleOptionDropdowns > 0) parts.Add($"🚨 {singleOptionDropdowns} dropdown option warning{(singleOptionDropdowns == 1 ? "" : "s")}");
        if (malformedDates > 0) parts.Add($"🚨 {malformedDates} malformed date{(malformedDates == 1 ? "" : "s")}");
        if (malformedPlaceholders > 0) parts.Add($"⚠ {malformedPlaceholders} placeholder defect{(malformedPlaceholders == 1 ? "" : "s")}");

        string details = string.Join(" · ", parts);
        return $"Markdown form field audit ({details} · {category} · {score:0.#}% compliance score)";
    }

}




/// <summary>

/// Represents the result of an audit for Git merge conflict markers and syntax ambiguities.
/// </summary>
public sealed record ConflictMarkerAndAmbiguityAudit(
    int ConflictMarkerCount,
    int AmbiguityCount,
    IReadOnlyList<string> ConflictLines,
    IReadOnlyList<string> Ambiguities,
    bool IsClean,
    string Summary);

/// <summary>
/// Represents a comprehensive multi-formula readability audit covering Flesch, Flesch-Kincaid,
/// Gunning Fog, Coleman-Liau, and ARI metrics.
/// </summary>
public sealed record ReadabilityMetricsAudit(
    double FleschReadingEase,
    double FleschKincaidGrade,
    double GunningFogIndex,
    double ColemanLiauIndex,
    double AutomatedReadabilityIndex,
    double ConsensusGrade,
    string ReadingEaseCategory,
    string AudienceCategory,
    int WordCount,
    int SentenceCount,
    int SyllableCount,
    int ComplexWordCount,
    int CharacterCount,
    TimeSpan EstimatedReadingTime,
    TimeSpan EstimatedSpeakingTime,
    string Summary);

/// <summary>
/// Represents a parsed markdown heading item with line number, depth level, and title text.
/// </summary>
public sealed record HeadingOutlineItem(
    int LineNumber,
    int Level,
    string Title);

/// <summary>
/// Represents the result of an audit of document heading outline balance and hierarchy depth distribution.
/// </summary>
public sealed record HeadingOutlineBalanceAudit(
    int TotalHeadings,
    int MinDepth,
    int MaxDepth,
    int SkippedLevelCount,
    IReadOnlyDictionary<int, int> LevelCounts,
    IReadOnlyList<string> SkippedWarnings,
    IReadOnlyList<HeadingOutlineItem> Headings,
    string BalanceCategory,
    string Summary);

/// <summary>
/// Represents the result of an audit of code fence language tagging coverage, untagged blocks, and inline code spans.
/// </summary>
public sealed record CodeFenceLanguageCoverageAudit(
    int TotalFencedBlocks,
    int TaggedFencedBlocks,
    int UntaggedFencedBlocks,
    int InlineCodeSpans,
    int TotalCodeLines,
    double TaggingCoveragePercentage,
    IReadOnlyDictionary<string, int> LanguageCounts,
    string CoverageCategory,
    string Summary);

/// <summary>
/// Represents the result of an audit of markdown list indentation hierarchy, nesting depth, and formatting consistency.
/// </summary>
public sealed record ListIndentationAudit(
    int TotalListItems,
    int OrderedItemCount,
    int UnorderedItemCount,
    int MaxDepth,
    double AverageIndentSpaces,
    int InconsistentIndentCount,
    int MixedListTypeCount,
    string StructureCategory,
    string Summary);

/// <summary>
/// Represents a parsed markdown table with line range, column counts, ragged rows, and alignment descriptors.
/// </summary>
public sealed record MarkdownTableInfo(
    int StartLine,
    int EndLine,
    int ExpectedColumnCount,
    int TotalRows,
    int RaggedRowCount,
    bool HasHeaderSeparator,
    IReadOnlyList<string> Alignments);

/// <summary>
/// Represents the result of an audit of markdown table column counts, pipe delimiters, and ragged row alignment.
/// </summary>
public sealed record TableColumnAlignmentAudit(
    int TotalTables,
    int TotalTableRows,
    int TotalRaggedRows,
    int MaxColumnsObserved,
    IReadOnlyList<MarkdownTableInfo> Tables,
    string AlignmentCategory,
    string Summary);

/// <summary>
/// Represents an individual parsed markdown task list checkbox item.
/// </summary>
public sealed record TaskListItem(
    int LineNumber,
    string Section,
    string TaskText,
    bool IsCompleted);

/// <summary>
/// Represents task completion progress metrics for an individual markdown section heading.
/// </summary>
public sealed record TaskListSectionProgress(
    string Section,
    int TotalTasks,
    int CompletedTasks,
    int PendingTasks,
    double CompletionPercentage);

/// <summary>
/// Represents the result of a comprehensive markdown task list and checkbox progress audit.
/// </summary>
public sealed record TaskListProgressAudit(
    int TotalTasks,
    int CompletedTasks,
    int PendingTasks,
    double CompletionPercentage,
    IReadOnlyList<TaskListSectionProgress> SectionProgress,
    IReadOnlyList<TaskListItem> PendingTasksSample,
    string VelocityCategory,
    string Summary);

/// <summary>
/// Represents an inline footnote reference [^id] with line number.
/// </summary>
public sealed record FootnoteSequenceItem(
    int LineNumber,
    string Id);

/// <summary>
/// Represents a footnote definition [^id]: text with line number and preview.
/// </summary>
public sealed record FootnoteDefinitionItem(
    int LineNumber,
    string Id,
    string PreviewText);

/// <summary>
/// Represents the result of a comprehensive footnote reference and definition linkage audit.
/// </summary>
public sealed record FootnoteLinkageAudit(
    int TotalReferences,
    int UniqueReferencedIds,
    int TotalDefinitions,
    int UniqueDefinedIds,
    IReadOnlyList<string> UnresolvedReferenceIds,
    IReadOnlyList<string> OrphanDefinitionIds,
    IReadOnlyList<string> DuplicateDefinitionIds,
    IReadOnlyList<FootnoteSequenceItem> References,
    IReadOnlyList<FootnoteDefinitionItem> Definitions,
    string LinkageCategory,
    string Summary);

/// <summary>
/// Represents an individual parsed YAML frontmatter property with line number and inferred type.
/// </summary>
public sealed record FrontmatterPropertyItem(
    int LineNumber,
    string Key,
    string RawValue,
    string InferredType);

/// <summary>
/// Represents the result of an audit of markdown YAML frontmatter schema compliance,
/// required key presence, and unexpected/custom properties.
/// </summary>
public sealed record FrontmatterSchemaAudit(
    bool HasFrontmatter,
    bool IsClosed,
    int KeyCount,
    IReadOnlyList<string> PresentKeys,
    IReadOnlyList<string> MissingRequiredKeys,
    IReadOnlyList<string> CustomKeys,
    IReadOnlyList<string> DuplicateKeys,
    IReadOnlyList<FrontmatterPropertyItem> Properties,
    string SchemaHealthCategory,
    double CompletenessPercentage,
    string Summary);

/// <summary>
/// Represents an individual parsed markdown heading with its outline level, title, and generated anchor slug.
/// </summary>
public sealed record HeadingAnchorItem(
    int LineNumber,
    int Level,
    string Title,
    string Slug,
    bool HasHierarchyJump,
    int JumpFromLevel);

/// <summary>
/// Represents an internal markdown in-document anchor link [text](#slug).
/// </summary>
public sealed record TocLinkItem(
    int LineNumber,
    string LinkText,
    string TargetSlug,
    bool IsValidHeadingTarget,
    bool IsSelfReferential);

/// <summary>
/// Represents the result of an audit of document Table of Contents (TOC) depth hierarchy consistency,
/// heading anchor slug collisions, and in-document anchor link validity.
/// </summary>
public sealed record TocAnchorAudit(
    int TotalHeadings,
    int MinHeadingLevel,
    int MaxHeadingLevel,
    int HierarchyJumpCount,
    int TotalAnchorLinks,
    int ValidAnchorLinks,
    int BrokenAnchorLinks,
    int SelfReferentialLinks,
    int DuplicateSlugCount,
    IReadOnlyList<HeadingAnchorItem> Headings,
    IReadOnlyList<TocLinkItem> AnchorLinks,
    IReadOnlyList<string> BrokenTargetSlugs,
    IReadOnlyList<string> DuplicateSlugs,
    string HealthCategory,
    double IntegrityPercentage,
    string Summary);

/// <summary>
/// Represents an individual parsed CommonMark reference link definition ([label]: destination).
/// </summary>
public sealed record ReferenceDefinitionItem(
    int LineNumber,
    string Label,
    string Destination,
    string Title);

/// <summary>
/// Represents an individual reference link usage in markdown text.
/// </summary>
public sealed record ReferenceLinkItem(
    int LineNumber,
    string Text,
    string TargetLabel,
    string ReferenceType,
    bool IsResolved);

/// <summary>
/// Represents the result of an audit of CommonMark reference-style link definitions,
/// target resolution, orphan definitions, and duplicate definitions.
/// </summary>
public sealed record ReferenceLinkIntegrityAudit(
    int TotalDefinitions,
    int UniqueDefinitions,
    int TotalReferences,
    int ResolvedReferences,
    int BrokenReferences,
    int OrphanDefinitions,
    int DuplicateDefinitions,
    double IntegrityScore,
    IReadOnlyList<ReferenceDefinitionItem> Definitions,
    IReadOnlyList<ReferenceLinkItem> References,
    IReadOnlyList<string> BrokenLabels,
    IReadOnlyList<string> OrphanLabels,
    IReadOnlyList<string> DuplicateLabels,
    string Category,
    string Summary);

/// <summary>
/// Represents an unbalanced delimiter occurrence or syntax ambiguity in markdown text.
/// </summary>
public sealed record DelimiterBalanceItem(
    int LineNumber,
    int Column,
    string Delimiter,
    string Description,
    string ContextSnippet,
    string ProposedAutoRepair);

/// <summary>
/// Represents the result of an audit of markdown syntax ambiguity, unmatched delimiter balance,
/// and auto-repair preview proposals.
/// </summary>
public sealed record SyntaxAmbiguityAudit(
    int TotalAmbiguities,
    int UnclosedCodeSpans,
    int UnclosedEmphasisPairs,
    int UnclosedMathDelimiters,
    int UnmatchedBrackets,
    double SyntaxIntegrityScore,
    IReadOnlyList<DelimiterBalanceItem> Ambiguities,
    string Category,
    string Summary);

/// <summary>
/// Represents an analyzed inline or display LaTeX / KaTeX math formula and its syntactic complexity.
/// </summary>
public sealed record MathFormulaItem(
    int LineNumber,
    string DelimiterType,
    string FormulaSnippet,
    bool IsBalanced,
    int CurlyBraceDepth,
    int OperatorCount,
    double ComplexityScore,
    IReadOnlyList<string> SyntaxIssues);

/// <summary>
/// Represents the result of an audit of document LaTeX math syntax, KaTeX delimiter pairing,
/// formula complexity metrics, and environment closure.
/// </summary>
public sealed record MathFormulaAudit(
    int TotalFormulas,
    int InlineFormulas,
    int DisplayFormulas,
    int ValidFormulas,
    int ProblematicFormulas,
    double MeanComplexityScore,
    IReadOnlyList<MathFormulaItem> Formulas,
    IReadOnlyList<string> GlobalIssues,
    string ComplexityCategory,
    double SyntaxIntegrityScore,
    string Summary);

/// <summary>
/// Represents column width, alignment, and cell padding metrics for a single table column.
/// </summary>
public sealed record TableColumnMetrics(
    int ColumnIndex,
    string Alignment,
    int HeaderWidth,
    int MaxCellWidth,
    int MinCellWidth,
    double MeanCellWidth,
    bool HasTruncationRisk,
    int UnpaddedPipesCount);

/// <summary>
/// Represents parsed metrics, alignment specs, and row integrity for an individual markdown table.
/// </summary>
public sealed record TableDetailInfo(
    int StartLine,
    int EndLine,
    int ColumnCount,
    int TotalRows,
    int RaggedRowCount,
    int CellCount,
    double AlignmentIntegrityScore,
    IReadOnlyList<TableColumnMetrics> Columns,
    IReadOnlyList<string> AlignmentSpecifiers,
    bool HasProperPadding);

/// <summary>
/// Represents the result of an audit of GFM markdown table column alignment consistency,
/// delimiter pipe padding, cell wrapping hazards, and ragged rows.
/// </summary>
public sealed record TableAlignmentAndSpacingAudit(
    int TotalTables,
    int TotalRows,
    int TotalColumns,
    int TotalCells,
    int RaggedRows,
    int UnpaddedPipes,
    double MeanAlignmentScore,
    IReadOnlyList<TableDetailInfo> Tables,
    IReadOnlyList<string> Issues,
    string Category,
    string Summary);

/// <summary>
/// Represents parsed metrics and indentation for an individual definition item.
/// </summary>
public sealed record DefinitionItemInfo(
    int LineNumber,
    string Term,
    string DefinitionSnippet,
    int HangingIndentSpaces,
    bool IsInsideDefListBlock,
    bool HasIrregularIndent);

/// <summary>
/// Represents the result of an audit of markdown definition list syntax,
/// hanging indent uniformity, and structural consistency.
/// </summary>
public sealed record DefinitionListAudit(
    int TotalLists,
    int TotalTerms,
    int TotalDefinitions,
    int FencedDefLists,
    int LooseDefLists,
    int IrregularIndentCount,
    double UniformityScore,
    IReadOnlyList<DefinitionItemInfo> Items,
    IReadOnlyList<string> Issues,
    string Category,
    string Summary);

/// <summary>
/// Represents an evaluated inline footnote reference anchor in markdown text.
/// </summary>
public sealed record FootnoteAnchorItem(
    int LineNumber,
    string Id,
    int AppearanceIndex,
    bool IsMonotonicOrder);

/// <summary>
/// Represents evaluated definition metrics for an individual footnote.
/// </summary>
public sealed record FootnoteDefinitionDetail(
    int LineNumber,
    string Id,
    string PreviewSnippet,
    int CharacterLength,
    int ReferenceCount,
    bool IsOrphan);

/// <summary>
/// Represents the result of an audit of markdown footnote reference anchors, definitions,
/// bi-directional linkage consistency, duplicate detection, and monotonic order.
/// </summary>
public sealed record FootnoteReferenceConsistencyAudit(
    int TotalAnchors,
    int UniqueAnchors,
    int TotalDefinitions,
    int UniqueDefinitions,
    int UnreferencedDefinitions,
    int MissingDefinitions,
    int DuplicateDefinitions,
    int NonMonotonicAnchorCount,
    double FootnoteConsistencyScore,
    IReadOnlyList<FootnoteAnchorItem> Anchors,
    IReadOnlyList<FootnoteDefinitionDetail> Definitions,
    IReadOnlyList<string> MissingDefinitionIds,
    IReadOnlyList<string> OrphanDefinitionIds,
    IReadOnlyList<string> DuplicateDefinitionIds,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, nesting depth, and formatting for an individual markdown admonition callout.
/// </summary>
public sealed record AdmonitionCalloutItem(
    int LineNumber,
    string Tag,
    string Title,
    int NestingDepth,
    bool HasProperSpacing,
    bool IsStandardType,
    bool IsFoldable,
    bool? FoldableExpanded,
    int ContentLineCount);

/// <summary>
/// Represents the result of an audit of markdown admonition callouts, syntax compliance,
/// nesting depth, foldability markers, and content body emptiness.
/// </summary>
public sealed record AdmonitionCalloutAudit(
    int TotalCallouts,
    int StandardCallouts,
    int CustomCallouts,
    int NestedCallouts,
    int MaxNestingDepth,
    int ImproperSpacingCount,
    int EmptyBodyCount,
    double ParityScore,
    IReadOnlyList<AdmonitionCalloutItem> Callouts,
    IReadOnlyList<string> Issues,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, alt-text presence, and URL validity for an individual markdown or HTML image.
/// </summary>
public sealed record ImageAccessibilityItem(
    int LineNumber,
    string AltText,
    string SourceUrl,
    bool IsHtmlTag,
    bool HasAltText,
    bool HasGenericAltText,
    bool IsLocalRelative,
    bool IsSecureWeb,
    bool IsInsecureHttp,
    bool IsDataUri,
    bool HasValidImageExtension,
    string? WarningMessage);

/// <summary>
/// Represents the result of an audit of markdown image accessibility, WCAG alt-text compliance,
/// standard image file extensions, and insecure HTTP vs secure HTTPS protocols.
/// </summary>
public sealed record ImageAccessibilityAudit(
    int TotalImages,
    int ValidAccessibleImages,
    int MissingAltCount,
    int GenericAltCount,
    int BrokenOrMalformedCount,
    int InsecureHttpCount,
    int DataUriCount,
    int LocalRelativeCount,
    double AccessibilityScore,
    IReadOnlyList<ImageAccessibilityItem> Images,
    IReadOnlyList<string> Issues,
    string ComplianceCategory,
    string Summary);

/// <summary>
/// Represents evaluated metrics, delimiter pairing, and macro syntax for an individual LaTeX math span.
/// </summary>
public sealed record MathDelimiterItem(
    int LineNumber,
    string DelimiterType,
    string Snippet,
    bool IsBalanced,
    int CurlyBraceDepth,
    bool HasInvalidSpacing,
    IReadOnlyList<string> UnrecognizedMacros,
    IReadOnlyList<string> Issues);

/// <summary>
/// Represents the result of an audit of markdown LaTeX math delimiters ($...$, $$...$$, \(...\), \[...\]),
/// delimiter pairing, unbalanced curly braces, KaTeX macro syntax, and spacing compliance.
/// </summary>
public sealed record MathDelimiterAndMacroAudit(
    int TotalMathSpans,
    int InlineMathCount,
    int DisplayMathCount,
    int ValidMathCount,
    int SyntaxIssueCount,
    int UnbalancedDelimiters,
    int UnbalancedBraces,
    int InvalidSpacingCount,
    int UnknownMacroCount,
    double KaTeXInteropScore,
    IReadOnlyList<MathDelimiterItem> MathItems,
    IReadOnlyList<string> GlobalIssues,
    string CompatibilityCategory,
    string Summary);

/// <summary>
/// Represents calculated reading metric values and interpretations for document comprehension.
/// </summary>
public sealed record ReadingGradeMetricInfo(
    string MetricName,
    double Value,
    string FormattedValue,
    string TargetBenchmark,
    string Interpretation);

/// <summary>
/// Represents evaluated metrics, syllable counts, readability scores, and estimated reading/speaking times for markdown text.
/// </summary>
public sealed record DocumentReadingEaseAudit(
    int TotalWords,
    int TotalSentences,
    int TotalSyllables,
    int MonosyllableWords,
    int PolysyllableWords,
    double FleschReadingEase,
    double FleschKincaidGradeLevel,
    double AverageWordsPerSentence,
    double AverageSyllablesPerWord,
    double SilentReadingTimeMinutes,
    double SpeakingTimeMinutes,
    IReadOnlyList<ReadingGradeMetricInfo> Metrics,
    string ReadabilityCategory,
    string Summary);

/// <summary>
/// Represents evaluated metrics, colon prefix validity, and indentation for an individual definition list term.
/// </summary>
public sealed record DefinitionListItem(
    int LineNumber,
    string Term,
    IReadOnlyList<string> Definitions,
    bool HasProperColonPrefix,
    bool HasBlankLineBefore,
    bool HasMultipleDefinitions,
    string SyntaxStatus);

/// <summary>
/// Represents the result of an audit of markdown definition lists and glossary terms (Term\n: Definition),
/// colon prefixes, irregular indentations, orphaned terms, and multi-definition syntax.
/// </summary>
public sealed record DefinitionListAndGlossaryAudit(
    int TotalTerms,
    int TotalDefinitions,
    int OrphanedTerms,
    int MultiDefinitionTerms,
    int StandardIndentationCount,
    int IrregularIndentationCount,
    double GlossaryConsistencyScore,
    IReadOnlyList<DefinitionListItem> Terms,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, indentation level, checkbox state, and hierarchy consistency for an individual markdown task list item.
/// </summary>
public sealed record TaskListHierarchyItem(
    int LineNumber,
    string TaskText,
    int IndentLevel,
    bool IsCompleted,
    int SubtaskCount,
    int CompletedSubtaskCount,
    bool HasInconsistentHierarchy,
    string HierarchyStatus);

/// <summary>
/// Represents the result of an audit of markdown task list progress, subtask nesting hierarchy,
/// completion velocity, and parent-child checkbox state consistency.
/// </summary>
public sealed record TaskListHierarchyAudit(
    int TotalTasks,
    int CompletedTasks,
    int RootTasks,
    int NestedSubtasks,
    int InconsistentParentCount,
    int MaxNestingDepth,
    double CompletionPercentage,
    double HierarchyConsistencyScore,
    IReadOnlyList<TaskListHierarchyItem> Tasks,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, column count, delimiter alignments, and ragged row count for an individual markdown table.
/// </summary>
public sealed record TableColumnAlignmentItem(
    int TableIndex,
    int StartLine,
    int ColumnCount,
    int RowCount,
    int RaggedRows,
    bool HasOuterPipes,
    IReadOnlyList<string> ColumnAlignments,
    string AlignmentPattern,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown table column alignment syntax, header delimiter rows (|:---|:---:|---:|),
/// outer border pipes, and ragged row column counts across the document.
/// </summary>
public sealed record TableColumnAlignmentSyntaxAudit(
    int TotalTables,
    int TotalColumns,
    int LeftAlignedCount,
    int CenterAlignedCount,
    int RightAlignedCount,
    int DefaultAlignedCount,
    int RaggedRowCount,
    int MissingOuterPipeCount,
    double AlignmentConsistencyScore,
    IReadOnlyList<TableColumnAlignmentItem> Tables,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, reference count, definition count, and pairing state for an individual footnote identifier.
/// </summary>
public sealed record FootnoteReferencePairingItem(
    string Identifier,
    int ReferenceCount,
    int DefinitionCount,
    bool IsDanglingReference,
    bool IsOrphanDefinition,
    bool HasDuplicateDefinitions,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown footnote reference anchors ([^label]) against definitions ([^label]: text),
/// detecting dangling references, orphan definitions, duplicates, and scoring document reference integrity.
/// </summary>
public sealed record FootnoteReferencePairingAudit(
    int TotalReferences,
    int UniqueReferences,
    int TotalDefinitions,
    int UniqueDefinitions,
    int DanglingReferences,
    int OrphanDefinitions,
    int DuplicateDefinitions,
    int MatchedPairs,
    double PairingIntegrityScore,
    IReadOnlyList<FootnoteReferencePairingItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated alt-text metrics, quality tier, and accessibility compliance for an individual markdown or HTML image embed.
/// </summary>
public sealed record DocumentImageAltItem(
    int LineNumber,
    string AltText,
    string ImageSource,
    bool IsEmpty,
    bool IsGenericPlaceholder,
    bool HasRedundantPrefix,
    bool IsOverlyLong,
    bool IsDataUri,
    string QualityTier,
    string Status);

/// <summary>
/// Represents the result of an audit of document image embeds for alt-text quality, meaningful descriptions,
/// empty caption violations, generic placeholder descriptors, and WCAG 2.1 Non-Text Content accessibility compliance.
/// </summary>
public sealed record DocumentImageAltTextAccessibilityAudit(
    int TotalImages,
    int DescriptiveCount,
    int EmptyCount,
    int GenericPlaceholderCount,
    int RedundantPrefixCount,
    int OverlyLongCount,
    int DataUriCount,
    double AccessibilityComplianceScore,
    IReadOnlyList<DocumentImageAltItem> Images,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated delimiter metrics, tag type, inner text, and syntax balance for an individual strikethrough or highlight span.
/// </summary>
public sealed record StrikethroughHighlightSpanItem(
    int LineNumber,
    string DelimiterType,
    string RawText,
    string InnerText,
    bool IsClosed,
    bool HasWhitespaceError,
    bool IsEmpty,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown strikethrough (~~text~~) and highlight (==text==) syntax balance,
/// unclosed spans, inner whitespace violations, and inline formatting health.
/// </summary>
public sealed record StrikethroughHighlightFormattingAudit(
    int TotalSpans,
    int StrikethroughCount,
    int HighlightCount,
    int BalancedSpanCount,
    int UnclosedSpanCount,
    int WhitespaceErrorCount,
    int EmptySpanCount,
    double FormattingBalanceScore,
    IReadOnlyList<StrikethroughHighlightSpanItem> Spans,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, nesting level, alert type, and citation status for an individual blockquote line.
/// </summary>
public sealed record BlockquoteCalloutItem(
    int LineNumber,
    int NestingDepth,
    string Text,
    bool IsAlertCallout,
    string AlertType,
    bool IsCitationFooter,
    bool HasTrailingEmptyQuote,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown blockquote nesting depth, GitHub-style alert callouts ([!NOTE], [!TIP], etc.),
/// citation footers, and structural ergonomics across the document.
/// </summary>
public sealed record BlockquoteNestingAndCalloutAudit(
    int TotalQuoteLines,
    int TotalQuoteBlocks,
    int MaxNestingDepth,
    int NoteAlertCount,
    int TipAlertCount,
    int ImportantAlertCount,
    int WarningAlertCount,
    int CautionAlertCount,
    int CitationCount,
    int NestedDeepCount,
    double ErgonomicsScore,
    IReadOnlyList<BlockquoteCalloutItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, definition count, indentation, and spacing compliance for an individual definition list term.
/// </summary>
public sealed record DefinitionListContinuityItem(
    int TermLineNumber,
    string Term,
    int DefinitionCount,
    bool HasPrecedingBlankLine,
    bool HasValidContinuation,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown definition list term spacing, blank line continuity, and description block integrity.
/// </summary>
public sealed record DefinitionListContinuityAudit(
    int TotalTerms,
    int TotalDefinitions,
    int OrphanTerms,
    int MultilineDefinitions,
    int SpacingIssues,
    double ContinuityScore,
    IReadOnlyList<DefinitionListContinuityItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, sequential ordering, and multi-paragraph continuation for an individual footnote reference.
/// </summary>
public sealed record FootnoteOrderItem(
    int LineNumber,
    string Label,
    int ReferenceOrder,
    bool IsNumeric,
    int? NumericValue,
    bool HasDefinition,
    int DefinitionLineNumber,
    int DefinitionParagraphCount,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown footnote sequential ordering, orphan references, unused definitions,
/// and multi-paragraph continuation blocks.
/// </summary>
public sealed record FootnoteOrderAndOrphanAudit(
    int TotalReferences,
    int TotalDefinitions,
    int OrphanReferences,
    int UnusedDefinitions,
    int OutOfOrderReferences,
    int MultiParagraphDefinitions,
    double IntegrityScore,
    IReadOnlyList<FootnoteOrderItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics and syntax integrity for an individual markdown math block.
/// </summary>
public sealed record InlineMathItem(
    int LineNumber,
    string FormulaType,
    string MathSnippet,
    bool IsBalanced,
    int UnclosedBraceCount,
    bool HasUnclosedDelimiter,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown inline and display math syntax, delimiter symmetry,
/// TeX brace balance, and currency collision detection.
/// </summary>
public sealed record MarkdownMathSyntaxAudit(
    int TotalMathBlocks,
    int InlineMathCount,
    int DisplayMathCount,
    int MalformedBlockCount,
    int UnclosedBraceCount,
    int CurrencyCollisionCount,
    double SyntaxIntegrityScore,
    IReadOnlyList<InlineMathItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, anchor slug, collision disambiguation, and TOC synchronization for a markdown heading.
/// </summary>
public sealed record HeaderAnchorItem(
    int LineNumber,
    int HeaderLevel,
    string HeaderText,
    string Slug,
    bool HasCollision,
    int CollisionIndex,
    bool IsReferencedInToc,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown header anchor slug collisions, duplicates,
/// and internal table-of-contents synchronization.
/// </summary>
public sealed record HeaderAnchorAndTocAudit(
    int TotalHeaders,
    int UniqueSlugs,
    int CollisionCount,
    int TocInternalLinksCount,
    int BrokenTocLinksCount,
    int UnreferencedHeadersCount,
    double AnchorSyncScore,
    IReadOnlyList<HeaderAnchorItem> Items,
    IReadOnlyList<string> BrokenTocSlugs,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, alert type, quote depth, accessibility description, and nesting compliance for an individual markdown callout block.
/// </summary>
public sealed record CalloutItem(
    int StartLine,
    int EndLine,
    string AlertType,
    int QuoteDepth,
    int ContentLineCount,
    int WordCount,
    bool HasValidType,
    bool IsNestedCallout,
    bool IsConsecutive,
    bool HasEmptyBody,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown callout alert syntax (> [!NOTE]), quote depth nesting,
/// consecutive alert placement, empty alert bodies, and accessibility descriptions.
/// </summary>
public sealed record MarkdownCalloutAudit(
    int TotalCallouts,
    int ValidCallouts,
    int NestedCallouts,
    int ConsecutiveCallouts,
    int EmptyBodyCallouts,
    int UnknownTypeCallouts,
    int MaxQuoteDepth,
    double SemanticIntegrityScore,
    IReadOnlyList<CalloutItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, alt-text quality, screen reader compliance, and caption metadata for an individual markdown image reference.
/// </summary>
public sealed record ImageCaptionAccessibilityItem(
    int LineNumber,
    string AltText,
    string Source,
    string? Title,
    bool HasEmptyAlt,
    bool IsGenericFilenameAlt,
    bool IsOverlyVerbose,
    bool HasCaption,
    bool IsDescriptive,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown image alt-text accessibility, screen reader contrast,
/// missing alt attributes, generic filename placeholders, and figure captions (WCAG compliance).
/// </summary>
public sealed record MarkdownImageAccessibilityAudit(
    int TotalImages,
    int DescriptiveAltCount,
    int EmptyAltCount,
    int GenericFilenameAltCount,
    int OverlyVerboseAltCount,
    int CaptionedCount,
    double AccessibilityScore,
    IReadOnlyList<ImageCaptionAccessibilityItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, label, target, orphan/dangling state, and cyclic loop status for an individual footnote or reference link item.
/// </summary>
public sealed record FootnoteReferenceDefinitionItem(
    int LineNumber,
    string Label,
    string ItemKind,
    string? TargetOrUrl,
    bool IsOrphanReference,
    bool IsDanglingDefinition,
    bool IsDuplicateDefinition,
    bool IsCyclicReference,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown footnote markers ([^1]), footnote definitions, reference link markers,
/// and reference link definitions ([label]: url), detecting orphans, dangling definitions, duplicates, and cyclic alias loops.
/// </summary>
public sealed record MarkdownFootnoteAndReferenceAudit(
    int TotalReferences,
    int TotalDefinitions,
    int ValidPairsCount,
    int OrphanReferencesCount,
    int DanglingDefinitionsCount,
    int DuplicateDefinitionsCount,
    int CyclicReferencesCount,
    double IntegrityScore,
    IReadOnlyList<FootnoteReferenceDefinitionItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, row/column counts, delimiter alignments, and ragged row count for an individual markdown table.
/// </summary>
public sealed record TableStructureItem(
    int TableIndex,
    int StartLine,
    int EndLine,
    int HeaderColumns,
    int TotalRows,
    int RaggedRows,
    int CellCount,
    int UnescapedPipes,
    bool HasOuterPipes,
    bool HasValidDelimiterRow,
    IReadOnlyList<string> ColumnAlignments,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown tables for header delimiter rows, column alignment syntax,
/// ragged row column parity, unescaped pipes, and outer border pipe consistency.
/// </summary>
public sealed record MarkdownTableAlignmentAudit(
    int TotalTables,
    int TotalRows,
    int TotalColumns,
    int RaggedRowsCount,
    int DelimiterIssuesCount,
    int UnescapedPipesCount,
    int OuterPipesMissingCount,
    double StructureComplianceScore,
    IReadOnlyList<TableStructureItem> Tables,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, priority, due date, overdue state, and completion status for an individual markdown task item.
/// </summary>
public sealed record TaskPriorityItem(
    int LineNumber,
    string Section,
    string Text,
    bool IsCompleted,
    string Priority,
    DateTime? DueDate,
    bool IsOverdue,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown task list items for priority tags ([p0]-[p3], [high], #urgent),
/// due date annotations (@due(YYYY-MM-DD), due:YYYY-MM-DD), overdue items, and task tracking rigor.
/// </summary>
public sealed record MarkdownTaskPriorityAudit(
    int TotalTasks,
    int CompletedTasks,
    int PendingTasks,
    int HighPriorityCount,
    int MediumPriorityCount,
    int LowPriorityCount,
    int OverdueCount,
    int TaggedTasksCount,
    double CompletionPercentage,
    double TrackingRigorScore,
    IReadOnlyList<TaskPriorityItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, alert tag, quote depth, empty body status, and attribution footer for an individual markdown blockquote item.
/// </summary>
public sealed record BlockquoteCalloutLevelItem(
    int LineNumber,
    int NestingDepth,
    string Text,
    bool IsAlertCallout,
    string AlertType,
    bool IsCustomCallout,
    bool HasExcessiveNesting,
    bool HasEmptyBody,
    bool HasAttribution,
    string? AttributionText,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown blockquote nesting levels, GitHub-style alert callouts ([!NOTE], [!TIP], etc.),
/// unclosed blocks, empty quote lines, and citation attribution footers.
/// </summary>
public sealed record MarkdownBlockquoteCalloutAudit(
    int TotalQuoteLines,
    int TotalQuoteBlocks,
    int MaxNestingDepth,
    int StandardAlertCount,
    int CustomAlertCount,
    int ExcessiveNestingCount,
    int EmptyQuoteCount,
    int AttributionCount,
    double StructuralRigorScore,
    IReadOnlyList<BlockquoteCalloutLevelItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, column offset, entity name, and encoding status for an individual HTML entity or character escape.
/// </summary>
public sealed record HtmlEntityEscapeItem(
    int LineNumber,
    int Column,
    string RawMatch,
    string Kind,
    string EntityOrEscapeName,
    bool IsValid,
    bool IsBroken,
    bool IsAmbiguous,
    string Recommendation,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown text for HTML entities, malformed entities missing semicolons,
/// ambiguous character escapes, and unescaped angle brackets.
/// </summary>
public sealed record MarkdownHtmlEntitiesAudit(
    int TotalEntities,
    int NamedEntityCount,
    int NumericEntityCount,
    int BrokenEntityCount,
    int AmbiguousEscapeCount,
    int UnescapedBracketCount,
    double HygieneScore,
    IReadOnlyList<HtmlEntityEscapeItem> Items,
    string Category,
    string Summary);



/// <summary>
/// Represents evaluated metrics, reference vs definition type, numeric sequence, and paired status for an individual markdown footnote item.
/// </summary>
public sealed record MarkdownFootnoteSequenceItem(
    int LineNumber,
    int Column,
    string Identifier,
    bool IsReference,
    bool IsDefinition,
    bool HasMatchingCounterpart,
    bool IsNumeric,
    int NumericSequence,
    bool InSequence,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown footnote references ([^1], [^note]), definitions ([^1]: text),
/// orphaned anchors, unreferenced definitions, and numeric sequence ordering.
/// </summary>
public sealed record MarkdownFootnoteAudit(
    int TotalReferences,
    int TotalDefinitions,
    int MatchedPairs,
    int OrphanedReferences,
    int OrphanedDefinitions,
    int SequentialMismatches,
    double StructuralIntegrityScore,
    IReadOnlyList<MarkdownFootnoteSequenceItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, nesting level, marker symbol, and sequential integrity for an individual markdown list item.
/// </summary>
public sealed record MarkdownListNestingItem(
    int LineNumber,
    int IndentSpaces,
    int NestingLevel,
    bool IsOrdered,
    string Marker,
    string Content,
    bool HasMarkerInconsistency,
    bool HasIndentationAnomaly,
    bool HasSequenceMismatch,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown unordered and ordered list nesting indentation,
/// marker sequence integrity, mixed list sibling symbols, and numeric continuity.
/// </summary>
public sealed record MarkdownListNestingAudit(
    int TotalListItems,
    int UnorderedCount,
    int OrderedCount,
    int MaxNestingDepth,
    int IndentationAnomalyCount,
    int MarkerInconsistencyCount,
    int SequenceMismatchCount,
    double ConsistencyScore,
    IReadOnlyList<MarkdownListNestingItem> Items,
    string Category,
    string Summary);




/// <summary>
/// Represents evaluated metrics, tag name, risk level, and dangerous attributes for an individual inline HTML tag.
/// </summary>
public sealed record MarkdownInlineHtmlSecurityItem(
    int LineNumber,
    int Column,
    string TagName,
    string RawTag,
    bool IsClosing,
    bool IsDangerousTag,
    bool HasDangerousAttributes,
    IReadOnlyList<string> DangerousAttributes,
    string RiskLevel,
    string Recommendation,
    string Status);

/// <summary>
/// Represents the result of an audit of markdown inline HTML tags, dangerous elements (<script>, <iframe>, <style>),
/// injection event handlers (onload, onclick), unclosed tags, and DOM security hygiene.
/// </summary>
public sealed record MarkdownInlineHtmlSecurityAudit(
    int TotalHtmlTags,
    int DangerousTagCount,
    int DangerousAttributeCount,
    int SafeTagCount,
    int UnclosedTagCount,
    double SecurityScore,
    IReadOnlyList<MarkdownInlineHtmlSecurityItem> Items,
    string Category,
    string Summary);


/// <summary>
/// Represents evaluated metrics, nesting level, completion state, and hygiene for an individual markdown task list item.
/// </summary>
public sealed record MarkdownTaskProgressionItem(
    int LineNumber,
    int IndentSpaces,
    int NestingLevel,
    bool IsCompleted,
    string Marker,
    string Description,
    bool IsEmpty,
    bool HasIndentationJump,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown task list checkbox progression, completion velocity, nesting hierarchy,
/// unlabeled tasks, and checklist tracking hygiene.
/// </summary>
public sealed record MarkdownTaskListProgressionAudit(
    int TotalTasks,
    int CompletedTasks,
    int PendingTasks,
    int EmptyTaskCount,
    int MaxNestingDepth,
    double CompletionRatio,
    double HygieneScore,
    IReadOnlyList<MarkdownTaskProgressionItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, definition vs reference usage type, target destination, and resolution status for a markdown link reference item.
/// </summary>
public sealed record MarkdownLinkReferenceItem(
    int LineNumber,
    int Column,
    string Label,
    string Destination,
    string Title,
    bool IsDefinition,
    bool IsUsage,
    string UsageType,
    bool IsResolved,
    bool IsUnreferenced,
    bool IsDuplicateDefinition,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown link reference definitions ([ref]: url), reference usages ([text][ref], [ref][], [ref]),
/// unreferenced definitions, broken unresolved reference links, duplicate labels, and reference resolution fidelity.
/// </summary>
public sealed record MarkdownLinkReferenceAudit(
    int TotalDefinitions,
    int TotalUsages,
    int ResolvedCount,
    int UnresolvedUsageCount,
    int UnreferencedDefinitionCount,
    int DuplicateDefinitionCount,
    double ResolutionFidelityScore,
    IReadOnlyList<MarkdownLinkReferenceItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, line number, identifier, and pairing status for an individual markdown footnote item.
/// </summary>
public sealed record MarkdownFootnoteDefinitionItem(
    int LineNumber,
    int Column,
    string Identifier,
    bool IsDefinition,
    bool IsCallout,
    bool IsResolved,
    bool IsOrphan,
    bool IsDuplicate,
    bool IsNumeric,
    int NumericValue,
    string Excerpt,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown footnote definitions ([^id]: text), footnote callouts ([^id]),
/// unreferenced orphan definitions, unresolved callouts, duplicate labels, and numbering sequence integrity.
/// </summary>
public sealed record MarkdownFootnoteDefinitionAudit(
    int TotalDefinitions,
    int TotalCallouts,
    int ResolvedCount,
    int UnresolvedCalloutCount,
    int OrphanDefinitionCount,
    int DuplicateDefinitionCount,
    int NumericFootnoteCount,
    bool IsMonotonicNumeric,
    double StructuralIntegrityScore,
    IReadOnlyList<MarkdownFootnoteDefinitionItem> Items,
    string Category,
    string Summary);



/// <summary>
/// Represents evaluated metrics, line number, delimiter type, and balance status for an individual markdown math item.
/// </summary>
public sealed record MarkdownMathDelimiterItem(
    int LineNumber,
    int Column,
    string DelimiterType,
    string MathExpression,
    bool IsClosed,
    bool IsBalanced,
    bool IsUnescapedCurrency,
    bool HasLeadingTrailingSpace,
    int UnbalancedBraceCount,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown inline and display math delimiters ($...$, $$...$$, \[...\], \(...\)),
/// unescaped currency dollar signs, delimiter pairing, whitespace compliance, and LaTeX equation rendering fidelity.
/// </summary>
public sealed record MarkdownMathDelimiterAudit(
    int TotalMathExpressions,
    int InlineMathCount,
    int DisplayMathCount,
    int ValidMathCount,
    int UnclosedDelimiterCount,
    int UnescapedCurrencyCount,
    int UnbalancedBraceCount,
    double MathRenderingFidelityScore,
    IReadOnlyList<MarkdownMathDelimiterItem> Items,
    string Category,
    string Summary);


/// <summary>
/// Represents evaluated metrics, line number, column, delimiter type, and closure status for an individual markdown strikethrough or underline span.
/// </summary>
public sealed record MarkdownStrikethroughItem(
    int LineNumber,
    int Column,
    string SpanType,
    string RawText,
    bool IsClosed,
    bool HasLeadingTrailingSpace,
    bool IsPseudoHtml,
    string Status,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown strikethrough (~~text~~), pseudo-HTML underline/del tags (&lt;u&gt;, &lt;del&gt;, &lt;s&gt;, &lt;ins&gt;),
/// unclosed delimiters, whitespace padding violations, and typographical consistency.
/// </summary>
public sealed record MarkdownStrikethroughAudit(
    int TotalSpans,
    int GfmStrikethroughCount,
    int PseudoHtmlCount,
    int ValidSpansCount,
    int UnclosedCount,
    int InvalidWhitespaceCount,
    double ConsistencyScore,
    IReadOnlyList<MarkdownStrikethroughItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, line number, task text, state, and issue for an individual markdown task list item.
/// </summary>
public sealed record MarkdownTaskListItem(
    int LineNumber,
    string RawLine,
    string TaskText,
    string State,
    string Severity,
    string Issue,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown task lists and checkboxes (- [ ], - [x]),
/// malformed brackets, orphaned items, completion metrics, and GFM syntax compliance.
/// </summary>
public sealed record MarkdownTaskListsAudit(
    int TotalTasks,
    int CompletedCount,
    int PendingCount,
    int MalformedCount,
    int OrphanedCount,
    double CompletionPercentage,
    double ComplianceScore,
    IReadOnlyList<MarkdownTaskListItem> Items,
    string Category,
    string Summary);


/// <summary>
/// Represents evaluated metrics, line number, identifier, and resolution status for an individual markdown footnote reference or definition.
/// </summary>
public sealed record MarkdownFootnoteIntegrityItem(
    int LineNumber,
    string Identifier,
    string ItemType,
    bool IsResolved,
    bool IsMissingDefinition,
    bool IsUnreferencedOrphan,
    bool IsDuplicate,
    string Excerpt,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown footnote references ([^1], [^note]) and definitions ([^1]: text),
/// unreferenced orphan definitions, missing definitions, duplicate labels, and sequential numeric integrity.
/// </summary>
public sealed record MarkdownFootnoteIntegrityAudit(
    int TotalDefinitions,
    int TotalReferences,
    int ResolvedCount,
    int MissingDefinitionCount,
    int UnreferencedDefinitionCount,
    int DuplicateDefinitionCount,
    bool IsSequentialNumeric,
    double StructuralIntegrityScore,
    IReadOnlyList<MarkdownFootnoteIntegrityItem> Items,
    string Category,
    string Summary);


/// <summary>
/// Represents evaluated metrics, line number, nesting depth, prefix, alert type, and structural health for a markdown blockquote line.
/// </summary>
public sealed record MarkdownBlockquoteNestingItem(
    int LineNumber,
    int NestingDepth,
    string Prefix,
    bool IsAlertCallout,
    string? AlertType,
    bool IsRaggedPrefix,
    bool IsNestedCallout,
    string Excerpt,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown blockquote nesting levels (> and >>), ragged prefixes,
/// standard blockquotes vs GFM alert callouts (> [!NOTE]), and quotation hierarchy structural integrity.
/// </summary>
public sealed record MarkdownBlockquoteNestingAudit(
    int TotalBlockquoteLines,
    int MaxNestingDepth,
    int StandardQuoteCount,
    int AlertCalloutCount,
    int NestedCalloutCount,
    int RaggedPrefixCount,
    double StructuralHierarchyScore,
    IReadOnlyList<MarkdownBlockquoteNestingItem> Items,
    string Category,
    string Summary);

/// <summary>
/// Represents evaluated metrics, line number, raw marker, style, surrounding blank line spacing, and recommendation for a markdown thematic break / horizontal rule.
/// </summary>
public sealed record MarkdownHorizontalRuleItem(
    int LineNumber,
    string RawMarker,
    string CanonicalType,
    bool HasPrecedingBlankLine,
    bool HasFollowingBlankLine,
    bool IsSetextOrTableAmbiguity,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown horizontal rules (---, ***, ___), surrounding blank line whitespace hygiene,
/// mixed marker styles, and thematic divider spacing consistency.
/// </summary>
/// <summary>
/// Represents a single definition list item in markdown glossary auditing.
/// </summary>
public sealed record MarkdownDefinitionListItem(
    int LineNumber,
    string Term,
    string DefinitionText,
    bool IsMultiLine,
    bool HasBlankLineSeparation,
    bool IsDuplicate,
    string Recommendation);

/// <summary>
/// Represents the result of an audit of markdown definition lists and glossary terms.
/// </summary>
public sealed record MarkdownDefinitionListAudit(
    int TotalTerms,
    int TotalDefinitions,
    int MultiLineDefinitions,
    int DuplicateTermCount,
    int OrphanDefinitionCount,
    double DefinitionHygieneScore,
    IReadOnlyList<MarkdownDefinitionListItem> Items,
    string Category,
    string Summary);


public sealed record MarkdownAbbreviationItem(
    int LineNumber,
    string Acronym,
    string? Expansion,
    int UsagesInDocument,
    bool HasDefinition,
    bool HasConflict,
    string Recommendation);

public sealed record MarkdownAbbreviationAudit(
    int TotalAbbreviations,
    int DefinedAcronyms,
    int UndefinedAcronyms,
    int ConflictingDefinitions,
    int TotalAcronymUsages,
    double ConsistencyScore,
    IReadOnlyList<MarkdownAbbreviationItem> Items,
    string Category,
    string Summary);

public sealed record MarkdownTableCellRowItem(
    int LineNumber,
    int TableIndex,
    int ExpectedColumns,
    int ActualColumns,
    bool HasOuterPipes,
    bool IsMismatched,
    string Recommendation);

public sealed record MarkdownTableCellAudit(
    int TotalTables,
    int TotalRows,
    int MismatchedRows,
    int MissingDelimiterTables,
    int UnbalancedOuterPipesCount,
    double TableHygieneScore,
    IReadOnlyList<MarkdownTableCellRowItem> Items,
    string Category,
    string Summary);

public sealed record MarkdownHorizontalRuleAudit(
    int TotalBreaks,
    int DashBreakCount,
    int AsteriskBreakCount,
    int UnderscoreBreakCount,
    int MissingBlankLineCount,
    int MixedMarkerStyleCount,
    double SpacingConsistencyScore,
    IReadOnlyList<MarkdownHorizontalRuleItem> Items,
    string Category,
    string Summary);

public sealed record MarkdownFootnoteBidirectionalItem(
    int LineNumber,
    string Identifier,
    string ItemType,
    bool IsResolved,
    bool IsMissingDefinition,
    bool IsUnreferencedDefinition,
    bool IsDuplicate,
    bool IsCircularReference,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownFootnoteBidirectionalAudit(
    int TotalReferences,
    int TotalDefinitions,
    int ResolvedPairs,
    int MissingDefinitionCount,
    int UnreferencedDefinitionCount,
    int DuplicateDefinitionCount,
    int CircularReferenceCount,
    double BidirectionalHealthScore,
    IReadOnlyList<MarkdownFootnoteBidirectionalItem> Items,
    string Category,
    string Summary);

public sealed record MarkdownInlineHtmlItem(
    int LineNumber,
    int Column,
    string MatchedText,
    string IssueType,
    string TagName,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownInlineHtmlAudit(
    int TotalAngleBracketConstructs,
    int AllowedSafeHtmlTags,
    int DangerousTagCount,
    int UnescapedRawAngleBracketCount,
    int UnclosedInlineTagCount,
    int DeprecatedTagCount,
    double HtmlSanitizationScore,
    IReadOnlyList<MarkdownInlineHtmlItem> Items,
    string Category,
    string Summary);

public sealed record MarkdownTaskFormattingItem(
    int LineNumber,
    string Text,
    string Marker,
    int IndentSpaces,
    int NestingLevel,
    bool IsCompleted,
    bool HasIndentationInconsistency,
    bool IsMalformedCheckbox,
    bool IsOrphanedCheckbox,
    string IssueDescription);

public sealed record MarkdownTaskFormattingAudit(
    int TotalTaskItems,
    int CompletedTasks,
    int PendingTasks,
    int InconsistentIndentCount,
    int MalformedCheckboxCount,
    int OrphanedCheckboxCount,
    int MaxNestingDepth,
    double FormattingHygieneScore,
    IReadOnlyList<MarkdownTaskFormattingItem> Items,
    string Category,
    string Summary);



public sealed record MarkdownTablePipeAlignmentItem(
    int LineNumber,
    int TableIndex,
    string RowType,
    int ColumnCount,
    int ExpectedColumns,
    bool HasMissingHeaderHyphens,
    bool HasUnescapedPipeInCode,
    bool HasDelimiterMisalignment,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownTablePipeAlignmentAudit(
    int TotalTables,
    int TotalRows,
    int AlignedTables,
    int MisalignedDelimiterRows,
    int ColumnCountMismatchRows,
    int MissingHeaderHyphenRows,
    int UnescapedPipesInCodeCount,
    double TableAlignmentScore,
    IReadOnlyList<MarkdownTablePipeAlignmentItem> Items,
    string Category,
    string Summary);



public sealed record MarkdownFootnoteParityItem(
    string Label,
    int LineNumber,
    bool IsDefinition,
    bool IsMissingDefinition,
    bool IsOrphanDefinition,
    bool IsDuplicateDefinition,
    bool IsNonSequentialNumeric,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownFootnoteParityAudit(
    int TotalReferences,
    int TotalDefinitions,
    int BalancedPairs,
    int MissingDefinitionCount,
    int OrphanDefinitionCount,
    int DuplicateDefinitionCount,
    int NonSequentialNumericCount,
    double FootnoteParityScore,
    IReadOnlyList<MarkdownFootnoteParityItem> Items,
    string Category,
    string Summary);



public sealed record MarkdownHtmlSanitizationItem(
    int LineNumber,
    int Column,
    string MatchedText,
    string HazardType,
    string TagName,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownHtmlSanitizationAudit(
    int TotalTags,
    int DangerousTagCount,
    int InlineEventHandlerCount,
    int DeprecatedTagCount,
    int UnclosedTagCount,
    double HtmlSanitizationScore,
    IReadOnlyList<MarkdownHtmlSanitizationItem> Items,
    string Category,
    string Summary);



public sealed record MarkdownYamlFrontmatterItem(
    int LineNumber,
    string KeyName,
    string IssueType,
    string Severity,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownYamlFrontmatterAudit(
    bool HasFrontmatter,
    int TotalKeys,
    int SyntaxErrorCount,
    int DuplicateKeyCount,
    int MissingRecommendedKeyCount,
    double FrontmatterScore,
    IReadOnlyList<MarkdownYamlFrontmatterItem> Items,
    string Category,
    string Summary);

public sealed record MarkdownTableColumnAlignmentStrictnessItem(
    int LineNumber,
    int TableIndex,
    string RowType,
    int ColumnCount,
    int ExpectedColumns,
    bool HasMissingOuterPipes,
    bool HasMalformedSeparator,
    bool HasColumnMismatch,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownTableColumnAlignmentStrictnessAudit(
    int TotalTables,
    int TotalRows,
    int AlignedTables,
    int ColumnMismatchCount,
    int MalformedSeparatorCount,
    int MissingOuterPipeCount,
    double TableFidelityScore,
    IReadOnlyList<MarkdownTableColumnAlignmentStrictnessItem> Items,
    string Category,
    string Summary);

public sealed record MarkdownFootnoteSequentialNumberingItem(
    string Label,
    int LineNumber,
    string IssueType,
    string Severity,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownFootnoteSequentialNumberingAudit(
    int TotalReferences,
    int TotalDefinitions,
    int SequentialNumericCount,
    int OrphanReferenceCount,
    int UnreferencedDefinitionCount,
    int DuplicateDefinitionCount,
    int DisorderedSequenceCount,
    double FootnoteIntegrityScore,
    IReadOnlyList<MarkdownFootnoteSequentialNumberingItem> Items,
    string Category,
    string Summary);

public sealed record MarkdownBlockquoteAlertCalloutItem(
    int LineNumber,
    string RawTag,
    string DetectedType,
    string IssueType,
    string Severity,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownBlockquoteAlertCalloutsAudit(
    int TotalCallouts,
    int ValidCanonicalCount,
    int ValidExtendedCount,
    int MissingExclamationCount,
    int UnrecognizedTagCount,
    int MalformedSyntaxCount,
    int EmptyCalloutCount,
    double AlertGrammarScore,
    IReadOnlyList<MarkdownBlockquoteAlertCalloutItem> Items,
    string Category,
    string Summary);

public sealed record MarkdownTaskListPriorityItem(
    int LineNumber,
    string RawTag,
    string PriorityLevel,
    string DeadlineDate,
    string IssueType,
    string Severity,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownTaskListPrioritiesAudit(
    int TotalTasks,
    int ValidPrioritizedTasks,
    int ValidDeadlines,
    int MalformedPriorities,
    int MalformedDeadlines,
    int UntaggedTasks,
    double TaskComplianceScore,
    IReadOnlyList<MarkdownTaskListPriorityItem> Items,
    string Category,
    string Summary);


public sealed record MarkdownFormFieldItem(
    int LineNumber,
    string RawTag,
    string FieldType,
    string IssueType,
    string Severity,
    string Excerpt,
    string Recommendation);

public sealed record MarkdownFormFieldsAudit(
    int TotalFields,
    int ValidDropdowns,
    int ValidDates,
    int ValidTexts,
    int SingleOptionDropdowns,
    int MalformedDates,
    int MalformedPlaceholders,
    double FormFieldComplianceScore,
    IReadOnlyList<MarkdownFormFieldItem> Items,
    string Category,
    string Summary);

