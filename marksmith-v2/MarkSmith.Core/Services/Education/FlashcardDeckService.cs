using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Education;

public record FlashcardItem(int CardIndex, string Front, string Back, string? Category = null);

public class FlashcardDeck
{
    public string DeckName { get; set; } = "Study Deck";
    public List<FlashcardItem> Cards { get; } = new();
}

/// <summary>
/// Service for extracting Q&A flashcards from Markdown and generating interactive flip-card study decks and Anki CSV exports.
/// </summary>
public static class FlashcardDeckService
{
    private static readonly Regex QaBlockRegex = new(
        @"^Q:\s*([^\r\n]+)\r?\n(?:A:\s*([^\r\n]+))",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FlashcardFenceRegex = new(
        @":::flashcard(?:\s+category=""([^""]+)"")?\r?\n([\s\S]*?)\r?\n---\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans Markdown for Q&A pairs and flashcard fences.
    /// </summary>
    public static FlashcardDeck ExtractDeck(string markdown, string defaultDeckName = "Study Deck")
    {
        var deck = new FlashcardDeck { DeckName = defaultDeckName };
        if (string.IsNullOrWhiteSpace(markdown))
            return deck;

        int index = 1;

        // 1. Q: ... A: ... pairs
        foreach (Match m in QaBlockRegex.Matches(markdown))
        {
            string q = m.Groups[1].Value.Trim();
            string a = m.Groups[2].Value.Trim();
            deck.Cards.Add(new FlashcardItem(index++, q, a));
        }

        // 2. :::flashcard fence syntax
        foreach (Match m in FlashcardFenceRegex.Matches(markdown))
        {
            string? cat = m.Groups[1].Success ? m.Groups[1].Value.Trim() : null;
            string front = m.Groups[2].Value.Trim();
            string back = m.Groups[3].Value.Trim();
            deck.Cards.Add(new FlashcardItem(index++, front, back, cat));
        }

        return deck;
    }

    /// <summary>
    /// Renders interactive HTML flip-card study deck markup.
    /// </summary>
    public static string RenderDeckHtml(FlashcardDeck deck)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<div class=\"ms-flashcard-deck\" data-deck=\"{System.Net.WebUtility.HtmlEncode(deck.DeckName)}\">");
        sb.AppendLine($"  <h3 class=\"ms-deck-title\">{System.Net.WebUtility.HtmlEncode(deck.DeckName)} ({deck.Cards.Count} cards)</h3>");
        sb.AppendLine("  <div class=\"ms-cards-grid\">");

        foreach (var card in deck.Cards)
        {
            sb.AppendLine($"    <div class=\"ms-card-wrapper\" onclick=\"this.classList.toggle('flipped')\">");
            sb.AppendLine("      <div class=\"ms-card-inner\">");
            sb.AppendLine($"        <div class=\"ms-card-front\"><strong>Q:</strong> {System.Net.WebUtility.HtmlEncode(card.Front)}</div>");
            sb.AppendLine($"        <div class=\"ms-card-back\"><strong>A:</strong> {System.Net.WebUtility.HtmlEncode(card.Back)}</div>");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    /// <summary>
    /// Exports flashcards into Anki-compatible tab-delimited text format.
    /// </summary>
    public static string ExportToAnkiTsv(FlashcardDeck deck)
    {
        var sb = new StringBuilder();
        foreach (var c in deck.Cards)
        {
            string frontClean = c.Front.Replace("\t", " ").Replace("\r", "").Replace("\n", "<br>");
            string backClean = c.Back.Replace("\t", " ").Replace("\r", "").Replace("\n", "<br>");
            sb.AppendLine($"{frontClean}\t{backClean}\t{c.Category ?? ""}");
        }
        return sb.ToString().TrimEnd();
    }
}
