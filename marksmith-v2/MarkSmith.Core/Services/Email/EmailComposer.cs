using System.Text.RegularExpressions;
using MarkSmith.Models;
using Markdig.Syntax;
using MimeKit;

namespace MarkSmith.Services.Email;

public sealed class EmailComposeRequest
{
    /// <summary>The document, already through <c>PrepareMarkdown</c> (AI cleanup and custom rules
    /// applied) — the composer never re-runs the cleanup.</summary>
    public string Markdown { get; init; } = "";

    /// <summary>File name or ingest label, for the {source} token and as a last-resort subject.</summary>
    public string? SourceLabel { get; init; }

    /// <summary>An explicit subject wins over the template.</summary>
    public string? Subject { get; init; }

    /// <summary>Raw recipient text ("Ann &lt;ann@x.com&gt;; bob@y.com"); null falls back to the
    /// Email settings.</summary>
    public string? To { get; init; }
    public string? Cc { get; init; }
    public string? Bcc { get; init; }

    public string? BaseDirectory { get; init; }
    public IReadOnlyList<byte[]?>? MermaidPngs { get; init; }
    public IReadOnlyList<EmailAttachment>? Attachments { get; init; }
    public bool IsDraft { get; init; } = true;
    public DateTime? Now { get; init; }
}

/// <summary>Markdown → <see cref="EmailDocument"/>: the subject, the recipients, the email-safe
/// body and its pictures. Pure and synchronous; the desktop app supplies the diagram PNGs it
/// rendered and any PDF / DOCX attachment, and a writer turns the result into a file. Every email
/// path is free on every plan (<see cref="FeatureId.EmailDraft"/>), so nothing here is gated.</summary>
public static class EmailComposer
{
    public const string DefaultSubjectTemplate = "{title}";

    public static EmailDocument Compose(EmailComposeRequest request, AppSettings settings, ThemeDefinition theme)
    {
        var renderer = new EmailHtmlRenderer(settings, theme, new EmailRenderOptions
        {
            RepeatTitleInBody = settings.EmailRepeatTitleInBody,
            BaseDirectory = request.BaseDirectory,
            MermaidPngs = request.MermaidPngs,
        });
        var rendered = renderer.Render(request.Markdown ?? "");

        var doc = new EmailDocument
        {
            Subject = !string.IsNullOrWhiteSpace(request.Subject)
                ? OneLine(request.Subject!)
                : BuildSubject(settings.EmailSubjectTemplate, rendered.Title, request.SourceLabel, request.Markdown, request.Now ?? DateTime.Now),
            HtmlBody = rendered.Html,
            TextBody = rendered.Text,
            IsDraft = request.IsDraft,
        };
        doc.InlineImages.AddRange(rendered.Images);
        doc.Notes.AddRange(rendered.Notes);

        AddRecipients(doc, doc.To, request.To ?? settings.EmailTo);
        AddRecipients(doc, doc.Cc, request.Cc ?? settings.EmailCc);
        AddRecipients(doc, doc.Bcc, request.Bcc ?? "");

        if (request.Attachments is { } atts) doc.Attachments.AddRange(atts);
        return doc;
    }

    private static void AddRecipients(EmailDocument doc, List<string> target, string? raw)
    {
        var (valid, invalid) = ParseAddresses(raw);
        target.AddRange(valid);
        foreach (var bad in invalid)
            doc.Notes.Add($"Left out \"{bad}\": it isn't an email address.");
    }

    /// <summary>Splits recipient text on ; , and new lines (commas inside "quoted names" stay put)
    /// and keeps each entry that is a real address. Returns the cleaned addresses and the rejects,
    /// so a settings box can flag the rejects as you type.</summary>
    public static (List<string> Valid, List<string> Invalid) ParseAddresses(string? raw)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return (valid, invalid);
        foreach (var entry in SplitRecipients(raw))
        {
            var e = entry.Trim();
            if (e.Length == 0) continue;
            if (MailboxAddress.TryParse(e, out var mb) && IsPlausible(mb.Address))
                valid.Add(mb.ToString());
            else
                invalid.Add(e);
        }
        return (valid, invalid);
    }

    private static IEnumerable<string> SplitRecipients(string raw)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var c in raw)
        {
            if (c == '"') quoted = !quoted;
            if (!quoted && (c == ';' || c == ',' || c == '\n' || c == '\r'))
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        yield return current.ToString();
    }

    // MimeKit accepts bare local parts ("bob") as addresses; a draft addressed to "bob" is a typo.
    private static bool IsPlausible(string address)
    {
        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 && address[(at + 1)..].Contains('.') && !address.Contains(' ');
    }

    /// <summary>Fills the subject template: {title} (the document's title), {source} (file name or
    /// ingest label) and {date}. A template whose title is missing falls back to the source, then
    /// to the document's opening words, so a draft never goes out as "(no subject)".</summary>
    public static string BuildSubject(string? template, string? title, string? source, string? markdown, DateTime now)
    {
        template = string.IsNullOrWhiteSpace(template) ? DefaultSubjectTemplate : template;
        var fallback = !string.IsNullOrWhiteSpace(title) ? title!
            : !string.IsNullOrWhiteSpace(source) ? source!
            : OpeningWords(markdown);
        var subject = template
            .Replace("{title}", fallback, StringComparison.OrdinalIgnoreCase)
            .Replace("{source}", source ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase);
        subject = OneLine(subject);
        // A template like "{source}: {title}" with no source leaves a dangling separator.
        subject = Regex.Replace(subject, @"^[\s:\-–—|·]+|[\s:\-–—|·]+$", "");
        return subject.Length > 0 ? subject : OneLine(fallback);
    }

    private static string OpeningWords(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var doc = Markdig.Markdown.Parse(markdown);
        foreach (var block in doc.Descendants<Markdig.Syntax.LeafBlock>())
        {
            if (block is Markdig.Syntax.CodeBlock || block.Inline is null) continue;
            var text = OneLine(EmailTextRenderer.InlineText(block.Inline));
            if (text.Length == 0) continue;
            if (text.Length <= 70) return text;
            var cut = text.LastIndexOf(' ', 70);
            return (cut > 30 ? text[..cut] : text[..70]).TrimEnd(',', ';', ':') + "…";
        }
        return "";
    }

    private static string OneLine(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();
}
