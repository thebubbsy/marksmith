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

        var lines = text.Split('\n');
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
}



