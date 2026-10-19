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
                Array.Empty<FootnoteReferenceItem>(), Array.Empty<FootnoteDefinitionItem>(),
                "No Footnotes", "Markdown footnotes: 0 items · Document contains no footnotes");
        }

        var lines = markdownText.Replace("\r", "").Split('\n');
        bool inCodeFence = false;
        bool inHtmlComment = false;

        var references = new List<FootnoteReferenceItem>();
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
                    references.Add(new FootnoteReferenceItem(i + 1, m.Groups[1].Value));
                }
            }
            else
            {
                var matches = refRegex.Matches(line);
                foreach (Match m in matches)
                {
                    references.Add(new FootnoteReferenceItem(i + 1, m.Groups[1].Value));
                }
            }
        }

        if (references.Count == 0 && definitions.Count == 0)
        {
            return new FootnoteLinkageAudit(
                0, 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<FootnoteReferenceItem>(), Array.Empty<FootnoteDefinitionItem>(),
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
        string category, IReadOnlyList<FootnoteReferenceItem>? references = null,
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
public sealed record FootnoteReferenceItem(
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
    IReadOnlyList<FootnoteReferenceItem> References,
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



