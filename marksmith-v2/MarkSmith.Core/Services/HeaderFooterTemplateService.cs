using System;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using MarkSmith.Models;

namespace MarkSmith.Services;

public sealed record HeaderFooterContext
{
    public string Title { get; init; } = "Marksmith Export";
    public string Author { get; init; } = "Marksmith";
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string FontSize { get; init; } = "9px";
    public string FontFamily { get; init; } = "Helvetica, Arial, sans-serif";
    public string TextColor { get; init; } = "#666666";
}

/// <summary>
/// Dynamic Header &amp; Footer Page Template Interpolator (Task 23). Converts template token strings
/// (e.g. "Page {page} of {pages} | {title} - {date}") into Chromium PDF-compatible header/footer HTML.
/// </summary>
public static class HeaderFooterTemplateService
{
    public static string RenderHtml(string template, HeaderFooterContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";

        var ctx = context ?? new HeaderFooterContext();

        var dateStr = ctx.Timestamp.ToString("yyyy-MM-dd");
        var timeStr = ctx.Timestamp.ToString("HH:mm");
        var escTitle = HtmlEncoder.Default.Encode(ctx.Title);
        var escAuthor = HtmlEncoder.Default.Encode(ctx.Author);

        var result = template
            .Replace("{page}", "<span class=\"pageNumber\"></span>", StringComparison.OrdinalIgnoreCase)
            .Replace("{pages}", "<span class=\"totalPages\"></span>", StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", escTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", dateStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", timeStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{author}", escAuthor, StringComparison.OrdinalIgnoreCase);

        return $"""
        <div style="font-size:{ctx.FontSize}; font-family:{ctx.FontFamily}; color:{ctx.TextColor}; width:100%; text-align:center; padding:0 15px; margin:0 auto; display:flex; justify-content:space-between; align-items:center;">
          <span>{result}</span>
        </div>
        """;
    }
}
