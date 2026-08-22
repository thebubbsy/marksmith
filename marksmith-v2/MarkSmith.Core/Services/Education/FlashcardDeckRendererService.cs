using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Education;

public record FlashcardStudyItem(string Question, string Answer);

public class FlashcardDeckModel
{
    public string Title { get; set; } = "Flashcard Deck";
    public List<FlashcardStudyItem> Cards { get; } = new();
}

/// <summary>
/// Service for parsing flashcard decks and rendering interactive 3D flip card components in HTML.
/// </summary>
public static class FlashcardDeckRendererService
{
    private static readonly Regex DeckFenceRegex = new(
        @":::flashcards(?:\s+([^\r\n]+))?\r?\n([\s\S]*?):::",
        RegexOptions.Compiled);

    /// <summary>
    /// Transforms all :::flashcards blocks into interactive 3D card deck widgets.
    /// </summary>
    public static string TransformFlashcardDecks(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        return DeckFenceRegex.Replace(markdown, match =>
        {
            string title = match.Groups[1].Success ? match.Groups[1].Value.Trim() : "Study Deck";
            string body = match.Groups[2].Value;

            var model = ParseDeck(body, title);
            return RenderDeckHtml(model);
        });
    }

    public static FlashcardDeckModel ParseDeck(string body, string title = "Flashcards")
    {
        var model = new FlashcardDeckModel { Title = title };
        var cardBlocks = body.Split(new[] { "---", "===" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in cardBlocks)
        {
            string b = block.Trim();
            if (string.IsNullOrEmpty(b)) continue;

            string q = "", a = "";
            var lines = b.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            bool inQ = false, inA = false;

            foreach (var line in lines)
            {
                string l = line.Trim();
                if (l.StartsWith("[Q]", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Q:", StringComparison.OrdinalIgnoreCase))
                {
                    inQ = true;
                    inA = false;
                    q = l.Substring(l.IndexOf(':') > 0 ? l.IndexOf(':') + 1 : 3).Trim();
                }
                else if (l.StartsWith("[A]", StringComparison.OrdinalIgnoreCase) || l.StartsWith("A:", StringComparison.OrdinalIgnoreCase))
                {
                    inA = true;
                    inQ = false;
                    a = l.Substring(l.IndexOf(':') > 0 ? l.IndexOf(':') + 1 : 3).Trim();
                }
                else if (inQ)
                {
                    q += " " + l;
                }
                else if (inA)
                {
                    a += " " + l;
                }
            }

            if (!string.IsNullOrEmpty(q) || !string.IsNullOrEmpty(a))
            {
                model.Cards.Add(new FlashcardStudyItem(q, a));
            }
        }

        return model;
    }

    public static string RenderDeckHtml(FlashcardDeckModel model)
    {
        var sb = new StringBuilder();
        string deckId = "fc_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        sb.AppendLine($"<div class=\"ms-flashcard-deck\" id=\"{deckId}\">");
        sb.AppendLine($"  <div class=\"ms-fc-header\"><span class=\"ms-fc-title\">{System.Net.WebUtility.HtmlEncode(model.Title)}</span> <span class=\"ms-fc-badge\">{model.Cards.Count} cards</span></div>");

        for (int i = 0; i < model.Cards.Count; i++)
        {
            var card = model.Cards[i];
            string display = i == 0 ? "flex" : "none";
            sb.AppendLine($"  <div class=\"ms-fc-card\" data-idx=\"{i}\" style=\"display: {display};\" onclick=\"this.classList.toggle('flipped')\">");
            sb.AppendLine("    <div class=\"ms-fc-inner\">");
            sb.AppendLine($"      <div class=\"ms-fc-front\"><div class=\"ms-fc-label\">QUESTION ({i + 1}/{model.Cards.Count})</div><div class=\"ms-fc-text\">{System.Net.WebUtility.HtmlEncode(card.Question)}</div><div class=\"ms-fc-hint\">Click to flip ↷</div></div>");
            sb.AppendLine($"      <div class=\"ms-fc-back\"><div class=\"ms-fc-label\">ANSWER</div><div class=\"ms-fc-text\">{System.Net.WebUtility.HtmlEncode(card.Answer)}</div><div class=\"ms-fc-hint\">Click to flip ↶</div></div>");
            sb.AppendLine("    </div>");
            sb.AppendLine("  </div>");
        }

        sb.AppendLine("</div>");
        return sb.ToString();
    }
}
