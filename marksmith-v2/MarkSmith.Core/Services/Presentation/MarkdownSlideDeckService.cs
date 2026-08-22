using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Services.Presentation;

public record SlideItem(
    int SlideIndex,
    string ContentMarkdown,
    string? Title = null,
    string? SpeakerNote = null,
    string? BackgroundColor = null,
    bool IsVertical = false);

public class SlideDeck
{
    public List<SlideItem> Slides { get; } = new();
    public string Title { get; set; } = "Presentation";
}

/// <summary>
/// Service that transforms Markdown documents into presentation slide decks and HTML5 presentations.
/// </summary>
public static class MarkdownSlideDeckService
{
    private static readonly Regex SlideDividerRegex = new(@"^(?:---|---|\*\*\*|<!--\s*slide\s*-->)\s*$", RegexOptions.Multiline);
    private static readonly Regex VerticalDividerRegex = new(@"^--\s*$", RegexOptions.Multiline);
    private static readonly Regex SpeakerNoteRegex = new(@"\?\?\?\s*(.*)$", RegexOptions.Singleline);
    private static readonly Regex BackgroundRegex = new(@"<!--\s*bg:\s*([#a-zA-Z0-9_\-]+)\s*-->", RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new(@"^#+\s+(.+)$", RegexOptions.Multiline);

    /// <summary>
    /// Parses a Markdown document into structured presentation slides.
    /// </summary>
    public static SlideDeck Parse(string markdown, string defaultTitle = "Presentation")
    {
        var deck = new SlideDeck { Title = defaultTitle };
        if (string.IsNullOrWhiteSpace(markdown))
            return deck;

        var rawSlides = SlideDividerRegex.Split(markdown);
        int slideIndex = 1;

        foreach (var rawSlide in rawSlides)
        {
            if (string.IsNullOrWhiteSpace(rawSlide))
                continue;

            // Check for vertical slides inside
            var subSlides = VerticalDividerRegex.Split(rawSlide);
            bool isFirstSub = true;

            foreach (var sub in subSlides)
            {
                if (string.IsNullOrWhiteSpace(sub)) continue;

                string content = sub.Trim();
                string? speakerNote = null;
                string? bgColor = null;
                string? title = null;

                // Extract speaker note
                var noteMatch = SpeakerNoteRegex.Match(content);
                if (noteMatch.Success)
                {
                    speakerNote = noteMatch.Groups[1].Value.Trim();
                    content = content.Substring(0, noteMatch.Index).Trim();
                }

                // Extract background color
                var bgMatch = BackgroundRegex.Match(content);
                if (bgMatch.Success)
                {
                    bgColor = bgMatch.Groups[1].Value;
                    content = content.Remove(bgMatch.Index, bgMatch.Length).Trim();
                }

                // Extract title
                var titleMatch = TitleRegex.Match(content);
                if (titleMatch.Success)
                {
                    title = titleMatch.Groups[1].Value.Trim();
                }

                deck.Slides.Add(new SlideItem(
                    slideIndex++,
                    content,
                    title,
                    speakerNote,
                    bgColor,
                    IsVertical: !isFirstSub));

                isFirstSub = false;
            }
        }

        if (deck.Slides.Count > 0 && !string.IsNullOrEmpty(deck.Slides[0].Title))
        {
            deck.Title = deck.Slides[0].Title!;
        }

        return deck;
    }

    /// <summary>
    /// Generates standalone HTML presentation slides markup.
    /// </summary>
    public static string GenerateHtmlPresentation(SlideDeck deck)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>{deck.Title}</title>");
        sb.AppendLine("""
            <style>
                body { margin: 0; padding: 0; background: #0f141c; color: #e6edf3; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; overflow: hidden; display: flex; align-items: center; justify-content: center; height: 100vh; }
                .slide-viewport { width: 90vw; max-width: 1200px; height: 80vh; background: #161b22; border: 1px solid #30363d; border-radius: 12px; padding: 48px; box-sizing: border-box; display: flex; flex-direction: column; justify-content: center; position: relative; box-shadow: 0 12px 32px rgba(0,0,0,0.5); }
                .slide-counter { position: absolute; bottom: 20px; right: 24px; font-size: 13px; color: #8b949e; }
                .slide-note { position: absolute; bottom: 20px; left: 24px; font-size: 13px; color: #58a6ff; }
                h1, h2 { color: #58a6ff; margin-top: 0; }
                pre { background: #0d1117; padding: 16px; border-radius: 8px; border: 1px solid #30363d; overflow: auto; }
            </style>
            </head><body>
            """);

        sb.AppendLine("<div class=\"slide-viewport\" id=\"active-slide\">");
        if (deck.Slides.Count > 0)
        {
            var first = deck.Slides[0];
            sb.AppendLine($"<div class=\"slide-content\">{first.ContentMarkdown}</div>");
            sb.AppendLine($"<div class=\"slide-counter\">1 / {deck.Slides.Count}</div>");
            if (!string.IsNullOrEmpty(first.SpeakerNote))
            {
                sb.AppendLine($"<div class=\"slide-note\">Note: {System.Net.WebUtility.HtmlEncode(first.SpeakerNote)}</div>");
            }
        }
        sb.AppendLine("</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
