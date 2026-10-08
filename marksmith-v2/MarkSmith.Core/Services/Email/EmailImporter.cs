using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using MarkSmith.Services.Import;
using MimeKit;
using MimeKit.Tnef;

namespace MarkSmith.Services.Email;

/// <summary>What to do with the quoted earlier messages under a reply.</summary>
public enum QuotedHistoryMode
{
    /// <summary>Fold them into a <c>&lt;details&gt;</c> block: one click away, out of the way.</summary>
    Collapse,
    /// <summary>Leave them out.</summary>
    Remove,
    /// <summary>Keep them inline, exactly as sent.</summary>
    Keep,
}

public sealed record EmailImportResult(
    string Markdown,
    string Subject,
    string? From,
    DateTimeOffset? Date,
    bool IsDraft,
    int InlineImages,
    IReadOnlyList<string> Attachments,
    string? MediaFolder,
    bool HadQuotedHistory)
{
    /// <summary>One status-bar line: who it's from, when, and what came with it.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            parts.Add(IsDraft ? "Opened email draft" : From is { Length: > 0 } f ? $"Opened email from {f}" : "Opened email");
            if (Date is { } d) parts.Add(d.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture));
            var saved = new List<string>();
            if (InlineImages > 0) saved.Add(InlineImages == 1 ? "1 image" : $"{InlineImages} images");
            if (Attachments.Count > 0) saved.Add(Attachments.Count == 1 ? "1 attachment" : $"{Attachments.Count} attachments");
            if (saved.Count > 0 && MediaFolder is not null)
                parts.Add($"{string.Join(" and ", saved)} saved to {Path.GetFileName(MediaFolder.TrimEnd('\\', '/'))}");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Opens an .eml as clean Markdown — the reverse of the email export:
/// - a header block (From / To / Cc / Date / attachments) under the subject as the H1; a draft
///   (<c>X-Unsent</c>, e.g. one MarkSmith wrote) gets just its subject;
/// - the HTML body through <see cref="HtmlToMarkdown"/> (plain text when there is no HTML;
///   Outlook's RTF-in-TNEF <c>winmail.dat</c> is unpacked first);
/// - inline <c>cid:</c> images and attachments saved to a <c>&lt;name&gt;_media</c> folder beside
///   the email (the same convention as the Word importer) and linked relatively;
/// - the quoted thread below the reply folded, removed or kept (<see cref="QuotedHistoryMode"/>);
/// - external-sender banners, "Sent from my iPhone" lines, tracking pixels and hidden preheaders
///   left out.</summary>
public static class EmailImporter
{
    public static EmailImportResult Import(string emlPath, QuotedHistoryMode history = QuotedHistoryMode.Collapse, string? mediaDir = null)
    {
        using var stream = File.OpenRead(emlPath);
        var message = MimeMessage.Load(stream);
        var stem = Path.GetFileNameWithoutExtension(emlPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(emlPath)) ?? ".";
        return Import(message, history, mediaDir ?? Path.Combine(dir, stem + "_media"), dir);
    }

    /// <param name="mediaDir">Where images and attachments are written (created only if needed).</param>
    /// <param name="documentDir">The folder the Markdown lives in; media links are relative to it
    /// when the media folder is inside it, absolute otherwise.</param>
    public static EmailImportResult Import(MimeMessage message, QuotedHistoryMode history, string? mediaDir, string? documentDir)
    {
        // Outlook sometimes sends its RTF body wrapped in TNEF (winmail.dat): unpack it first.
        var tnef = message.BodyParts.OfType<TnefPart>().FirstOrDefault();
        if (tnef is not null && message.HtmlBody is null && message.TextBody is null)
        {
            try
            {
                var inner = tnef.ConvertToMessage();
                foreach (var h in new[] { "From", "To", "Cc", "Date", "Subject" })
                    if (inner.Headers[h] is null && message.Headers[h] is { } v) inner.Headers[h] = v;
                message = inner;
            }
            catch { /* fall through with what we have */ }
        }

        bool isDraft = message.Headers["X-Unsent"]?.Trim() == "1";
        var subject = (message.Subject ?? "").Trim();
        var media = new MediaWriter(mediaDir, documentDir);

        // Parts referenced from the HTML by cid: become inline images; everything else that is a
        // file is an attachment.
        var byCid = new Dictionary<string, MimePart>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in message.BodyParts.OfType<MimePart>())
            if (!string.IsNullOrEmpty(part.ContentId)) byCid[part.ContentId.Trim('<', '>')] = part;

        var usedCids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string body;
        bool hadHistory;
        var html = message.HtmlBody;
        if (!string.IsNullOrWhiteSpace(html))
        {
            var options = new HtmlToMarkdownOptions
            {
                ResolveImage = src =>
                {
                    if (!src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                        return src.StartsWith("http", StringComparison.OrdinalIgnoreCase) || src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ? src : null;
                    var cid = Uri.UnescapeDataString(src[4..]).Trim('<', '>');
                    if (!byCid.TryGetValue(cid, out var part)) return null;
                    usedCids.Add(cid);
                    return media.Write(part, "image");
                },
            };
            (body, hadHistory) = ConvertHtml(html, history, options);
        }
        else
        {
            (body, hadHistory) = ConvertText(message.TextBody ?? "", history);
        }
        body = StripSignOffJunk(body);

        // Attachments: every remaining file part (not the bodies, not an inline image we used).
        var attachments = new List<(string Name, string Link, long Size)>();
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (part is TextPart tp && !tp.IsAttachment) continue;
            if (part is TnefPart) continue;
            var cid = part.ContentId?.Trim('<', '>');
            if (cid is not null && usedCids.Contains(cid)) continue;
            if (!part.IsAttachment && part.ContentType.MediaType != "application" && part.ContentType.MediaType != "image") continue;
            var name = part.FileName ?? part.ContentType.Name ?? (part.ContentType.MimeType.Replace('/', '.'));
            var link = media.Write(part, "attachment");
            if (link is null) continue;
            attachments.Add((name, link, media.LastSize));
        }

        var sb = new StringBuilder();
        var bodyStartsWithTitle = Regex.IsMatch(body, @"^#\s+" + Regex.Escape(EscapeInline(subject)) + @"\s*(\n|$)");
        if (subject.Length > 0 && !bodyStartsWithTitle) sb.Append("# ").Append(EscapeInline(subject)).Append("\n\n");

        if (!isDraft)
        {
            var lines = new List<string>();
            void Field(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) lines.Add($"**{label}:** {value}");
            }
            Field("From", Addresses(message.From));
            Field("To", Addresses(message.To));
            Field("Cc", Addresses(message.Cc));
            if (message.Date != DateTimeOffset.MinValue)
                Field("Date", message.Date.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture));
            if (attachments.Count > 0)
                Field("Attachments", string.Join(", ", attachments.Select(a => $"[{EscapeInline(a.Name)}]({LinkTarget(a.Link)}) ({FormatSize(a.Size)})")));
            if (lines.Count > 0) sb.Append(string.Join("\\\n", lines)).Append("\n\n---\n\n");
        }
        else if (attachments.Count > 0)
        {
            sb.Append("**Attachments:** ")
              .Append(string.Join(", ", attachments.Select(a => $"[{EscapeInline(a.Name)}]({LinkTarget(a.Link)}) ({FormatSize(a.Size)})")))
              .Append("\n\n");
        }

        sb.Append(body);
        var markdown = HtmlToMarkdown.Tidy(sb.ToString());
        if (markdown.Length == 0) markdown = "# " + (subject.Length > 0 ? subject : "Untitled email") + "\n";

        var from = message.From.Mailboxes.FirstOrDefault();
        return new EmailImportResult(
            markdown,
            subject,
            from is null ? null : (string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name),
            message.Date == DateTimeOffset.MinValue ? null : message.Date,
            isDraft,
            usedCids.Count,
            attachments.Select(a => a.Name).ToList(),
            media.Created ? mediaDir : null,
            hadHistory);
    }

    // ---------- HTML ----------

    // Where the quoted earlier messages start, per mail client.
    private static readonly string[] HistorySelectors =
    {
        "#divRplyFwdMsg",                              // Outlook on the web / new Outlook
        "#appendonsend",                               // OWA marks the end of the new text
        "#mail-editor-reference-message-container",    // new Outlook
        "div.gmail_quote", "blockquote.gmail_quote",   // Gmail
        "div.moz-cite-prefix",                         // Thunderbird
        "blockquote[type=cite]",                       // Apple Mail
        "div.yahoo_quoted", "#yahoo_quoted",           // Yahoo
    };

    private static readonly Regex OutlookFromBlock = new(@"^\s*From:\s*\S", RegexOptions.Compiled);
    private static readonly Regex OriginalMessage = new(@"^\s*-{2,}\s*(Original Message|Forwarded message)\s*-{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static (string Markdown, bool HadHistory) ConvertHtml(string html, QuotedHistoryMode mode, HtmlToMarkdownOptions options)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var body = doc.Body;
        if (body is null) return (HtmlToMarkdown.Convert(html, options), false);

        RemoveExternalBanners(body);

        var marker = FindHistoryStart(body);
        if (marker is null) return (HtmlToMarkdown.Convert(body, options), false);

        if (mode == QuotedHistoryMode.Keep)
            return (HtmlToMarkdown.Convert(body, options), true);

        // Move the marker and everything after it (in document order) into its own container.
        var history = doc.CreateElement("div");
        INode? node = marker;
        bool first = true;
        while (node is not null && node != body)
        {
            var parent = node.Parent;
            var start = first ? node : node.NextSibling;
            var move = new List<INode>();
            for (var n = start; n is not null; n = n.NextSibling) move.Add(n);
            foreach (var n in move) history.AppendChild(n);
            first = false;
            node = parent;
        }

        var main = HtmlToMarkdown.Convert(body, options);
        if (mode == QuotedHistoryMode.Remove) return (main, true);

        var quoted = HtmlToMarkdown.Convert(history, options).Trim();
        if (quoted.Length == 0) return (main, false);
        return (main.TrimEnd() + "\n\n" + Fold(quoted), true);
    }

    private static IElement? FindHistoryStart(IElement body)
    {
        IElement? best = null;
        void Consider(IElement? e)
        {
            if (e is null) return;
            if (best is null || (best.CompareDocumentPosition(e) & DocumentPositions.Preceding) != 0) best = e;
        }
        foreach (var sel in HistorySelectors) Consider(body.QuerySelector(sel));

        // Classic Outlook: <div style="border:none;border-top:solid #E1E1E1 1.0pt…"><p><b>From:</b> …
        foreach (var div in body.QuerySelectorAll("div"))
        {
            var style = div.GetAttribute("style") ?? "";
            if (Regex.IsMatch(style, @"border-top\s*:\s*solid", RegexOptions.IgnoreCase)
                && OutlookFromBlock.IsMatch(Clean(div.TextContent)))
            {
                // The rule above the header (OWA puts an <hr> before it) belongs to the history too.
                var start = div.PreviousElementSibling is { LocalName: "hr" } hr ? hr : div;
                Consider(start);
                break;
            }
        }
        // "-----Original Message-----" written as a paragraph.
        foreach (var p in body.QuerySelectorAll("p, div"))
        {
            if (p.Children.Length > 3) continue;
            if (OriginalMessage.IsMatch(Clean(p.TextContent))) { Consider(p); break; }
        }
        return best;
    }

    private static readonly Regex BannerText = new(
        @"(this (e-?mail|message) (originated|came|was sent) from outside|you don'?t often get e-?mail from|^\s*\[?external( e-?mail| sender)?\]?\s*[:\-]|caution\s*:?\s*(external|this e-?mail)|be careful with this message|this is an external e-?mail)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // External-sender warnings that IT departments and Outlook's safety tip prepend to mail.
    private static void RemoveExternalBanners(IElement body)
    {
        var total = Clean(body.TextContent);
        var hits = new List<IElement>();
        foreach (var e in body.QuerySelectorAll("table, div, p, span"))
        {
            var text = Clean(e.TextContent);
            if (text.Length == 0 || text.Length > 400 || !BannerText.IsMatch(text)) continue;
            // Never take the whole message with it: something real must remain outside.
            if (total.Length - text.Length < 20) continue;
            if (total.IndexOf(text, StringComparison.Ordinal) > 600) continue; // banners sit at the top
            hits.Add(e);
        }
        // The outermost qualifying element of each banner goes (its descendants go with it).
        foreach (var e in hits)
            if (!hits.Any(o => o != e && o.Contains(e))) e.Remove();
    }

    private static string Clean(string s) => Regex.Replace(s.Replace(' ', ' '), @"\s+", " ").Trim();

    // ---------- plain text ----------

    private static (string Markdown, bool HadHistory) ConvertText(string text, QuotedHistoryMode mode)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int cut = -1;
        for (int i = 0; i < lines.Length && cut < 0; i++)
        {
            var l = lines[i].Trim();
            if (OriginalMessage.IsMatch(l) || Regex.IsMatch(l, @"^_{10,}$")) cut = i;
            else if (Regex.IsMatch(l, @"^On .{4,200} wrote:$")) cut = i;
            else if (Regex.IsMatch(l, @"^From:\s*\S") && lines.Skip(i + 1).Take(4).Any(x => Regex.IsMatch(x.Trim(), @"^(Sent|Date):\s"))) cut = i;
            else if (l.StartsWith('>') && lines.Skip(i).All(x => x.Trim().Length == 0 || x.TrimStart().StartsWith('>'))) cut = i;
        }

        if (cut < 0 || mode == QuotedHistoryMode.Keep)
            return (HtmlToMarkdown.FromPlainText(text), cut >= 0);

        var main = HtmlToMarkdown.FromPlainText(string.Join("\n", lines.Take(cut)));
        if (mode == QuotedHistoryMode.Remove) return (main, true);
        var quoted = HtmlToMarkdown.FromPlainText(string.Join("\n", lines.Skip(cut))).Trim();
        return (main.TrimEnd() + "\n\n" + Fold(quoted), true);
    }

    private static string Fold(string quoted) =>
        "<details>\n<summary>Earlier in this thread</summary>\n\n" + quoted + "\n\n</details>\n";

    // "Sent from my iPhone" and friends, on a line of their own.
    private static readonly Regex SignOffJunk = new(
        @"^\**[ \t]*(Sent from my (iPhone|iPad|Android( phone| device)?|Samsung[\w ]*|Galaxy[\w ]*|mobile( device)?|BlackBerry[\w ]*)|Sent from (Mail|Outlook) for (Windows|iOS|Android|Mac)|Get Outlook for (iOS|Android|Mac))\.?[ \t]*\**[ \t]*(\\)?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static string StripSignOffJunk(string md)
    {
        // Only in the new message, not inside the folded thread.
        var at = md.IndexOf("<details>", StringComparison.Ordinal);
        var head = at < 0 ? md : md[..at];
        var tail = at < 0 ? "" : md[at..];
        head = SignOffJunk.Replace(head, "");
        head = Regex.Replace(head, @"\\\n(\s*\n)", "\n$1"); // a hard break left dangling before the gap
        return head + tail;
    }

    // ---------- helpers ----------

    private static string Addresses(InternetAddressList list)
    {
        var parts = new List<string>();
        foreach (var a in list)
        {
            if (a is MailboxAddress m)
            {
                var name = (m.Name ?? "").Trim();
                // A name with no address (an Exchange-only recipient) is just the name.
                parts.Add(name.Length == 0 || name.Equals(m.Address, StringComparison.OrdinalIgnoreCase)
                    ? m.Address
                    : string.IsNullOrWhiteSpace(m.Address) ? EscapeInline(name)
                    : $"{EscapeInline(name)} ({m.Address})");
            }
            else if (a is GroupAddress g)
            {
                parts.Add(EscapeInline(g.Name ?? ""));
            }
        }
        return string.Join("; ", parts);
    }

    private static string EscapeInline(string s) =>
        Regex.Replace(s, @"([\\`*_\[\]<>])", @"\$1");

    private static string LinkTarget(string url) =>
        url.IndexOfAny(new[] { ' ', '(', ')' }) >= 0 ? "<" + url + ">" : url;

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{Math.Max(1, bytes / 1024)} KB",
        _ => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
    };

    // Writes parts into the media folder once (re-opening the same email reuses the files).
    private sealed class MediaWriter
    {
        private readonly string? _dir;
        private readonly string? _docDir;
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        public bool Created { get; private set; }
        public long LastSize { get; private set; }

        public MediaWriter(string? dir, string? docDir) { _dir = dir; _docDir = docDir; }

        public string? Write(MimePart part, string fallbackStem)
        {
            if (_dir is null || part.Content is null) return null;
            var name = SafeName(part.FileName ?? part.ContentType.Name);
            if (name.Length == 0)
            {
                var ext = MimeTypes.TryGetExtension(part.ContentType.MimeType, out var e) ? e : ".bin";
                name = fallbackStem + ext;
            }
            name = Unique(name);

            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            var bytes = ms.ToArray();
            LastSize = bytes.LongLength;
            var path = Path.Combine(_dir, name);
            try
            {
                Directory.CreateDirectory(_dir);
                if (!File.Exists(path) || new FileInfo(path).Length != bytes.LongLength)
                    File.WriteAllBytes(path, bytes);
                Created = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Someone else holds it (the same email opened twice at once, or it's open in a
                // viewer): the file already there is the one we'd have written.
                if (!File.Exists(path) || new FileInfo(path).Length != bytes.LongLength) return null;
                Created = true;
            }

            if (_docDir is not null)
            {
                var rel = Path.GetRelativePath(_docDir, path);
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                    return rel.Replace('\\', '/');
            }
            return path;
        }

        private string Unique(string name)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            var candidate = name;
            for (int i = 2; !_names.Add(candidate); i++) candidate = $"{stem} ({i}){ext}";
            return candidate;
        }

        private static string SafeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            name = Path.GetFileName(name.Replace('/', '\\'));
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim().TrimEnd('.');
        }
    }
}
