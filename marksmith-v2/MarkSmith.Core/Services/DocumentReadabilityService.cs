using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MarkSmith.Services;

public record ReadabilityScore(
    double FleschReadingEase,
    double FleschKincaidGradeLevel,
    double GunningFogIndex,
    int TotalWords,
    int TotalSentences,
    int TotalSyllables,
    int ComplexWordsCount,
    string ReadabilityRating);

/// <summary>
/// Service that calculates structural readability metrics (Flesch Reading Ease, Flesch-Kincaid, Gunning Fog) for Markdown documents.
/// </summary>
public static class DocumentReadabilityService
{
    private static readonly Regex SentenceSplitRegex = new(@"[.!?]+(?:\s+|$)", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new(@"\b[a-zA-Z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex VowelRegex = new(@"[aeiouy]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MarkdownCharsRegex = new(@"[#>*_~`\[\]()!|\\-]", RegexOptions.Compiled);

    /// <summary>
    /// Analyzes the text and calculates standardized readability metrics.
    /// </summary>
    public static ReadabilityScore Analyze(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new ReadabilityScore(100, 0, 0, 0, 0, 0, 0, "Very Easy");

        // Clean Markdown formatting for accurate text analysis
        string plainText = StripMarkdown(markdown);

        var sentences = SentenceSplitRegex.Split(plainText).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        int sentenceCount = Math.Max(1, sentences.Count);

        var wordMatches = WordRegex.Matches(plainText);
        int wordCount = Math.Max(1, wordMatches.Count);

        int totalSyllables = 0;
        int complexWords = 0;

        foreach (Match match in wordMatches)
        {
            string word = match.Value;
            int syllables = CountSyllables(word);
            totalSyllables += syllables;
            if (syllables >= 3)
            {
                complexWords++;
            }
        }

        double wordsPerSentence = (double)wordCount / sentenceCount;
        double syllablesPerWord = (double)totalSyllables / wordCount;
        double percentComplex = ((double)complexWords / wordCount) * 100.0;

        // Flesch Reading Ease: 206.835 - 1.015 * (words/sentence) - 84.6 * (syllables/word)
        double fleschEase = Math.Round(Math.Clamp(206.835 - (1.015 * wordsPerSentence) - (84.6 * syllablesPerWord), 0.0, 100.0), 1);

        // Flesch-Kincaid Grade Level: 0.39 * (words/sentence) + 11.8 * (syllables/word) - 15.59
        double fkGrade = Math.Round(Math.Max(0.0, (0.39 * wordsPerSentence) + (11.8 * syllablesPerWord) - 15.59), 1);

        // Gunning Fog Index: 0.4 * ((words/sentence) + 100 * (complex words / words))
        double gunningFog = Math.Round(Math.Max(0.0, 0.4 * (wordsPerSentence + percentComplex)), 1);

        string rating = fleschEase switch
        {
            >= 90 => "Very Easy (5th grade)",
            >= 80 => "Easy (6th grade)",
            >= 70 => "Fairly Easy (7th grade)",
            >= 60 => "Standard / Plain English (8th-9th grade)",
            >= 50 => "Fairly Difficult (10th-12th grade)",
            >= 30 => "Difficult (College)",
            _ => "Very Difficult (Academic/Technical)"
        };

        return new ReadabilityScore(
            fleschEase,
            fkGrade,
            gunningFog,
            wordCount,
            sentenceCount,
            totalSyllables,
            complexWords,
            rating);
    }

    /// <summary>
    /// Estimates the syllable count of an English word.
    /// </summary>
    public static int CountSyllables(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return 0;
        word = word.Trim().ToLowerInvariant();
        if (word.Length <= 3) return 1;

        // Strip non-syllable endings
        if (word.EndsWith("e") && !word.EndsWith("le"))
            word = word.Substring(0, word.Length - 1);
        else if (word.EndsWith("ed") && !word.EndsWith("ded") && !word.EndsWith("ted"))
            word = word.Substring(0, word.Length - 2);

        var matches = VowelRegex.Matches(word);
        int count = matches.Count;
        return Math.Max(1, count);
    }

    private static string StripMarkdown(string md)
    {
        return MarkdownCharsRegex.Replace(md, " ");
    }
}
