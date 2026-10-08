using System.Buffers.Binary;
using MimeKit;
using OpenMcdf;
using OutlookMessage = MsgReader.Outlook.Storage.Message;
using OutlookAttachment = MsgReader.Outlook.Storage.Attachment;
using OutlookRecipientType = MsgReader.Outlook.RecipientType;

namespace MarkSmith.Services.Email;

/// <summary>Opens an Outlook message (.msg — what classic Outlook saves and what dragging a mail
/// out of it produces) by reading it with MSGReader and handing it to <see cref="EmailImporter"/>
/// as a MIME message. So a .msg gets exactly what an .eml gets: the header block, the quoted
/// thread folded, inline pictures and attachments saved beside it. MSGReader also turns an
/// RTF-only body (common in classic Outlook) into HTML.</summary>
public static class MsgImporter
{
    public static EmailImportResult Import(string msgPath, QuotedHistoryMode history = QuotedHistoryMode.Collapse, string? mediaDir = null)
    {
        var message = ToMimeMessage(msgPath);
        var stem = Path.GetFileNameWithoutExtension(msgPath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(msgPath)) ?? ".";
        return EmailImporter.Import(message, history, mediaDir ?? Path.Combine(dir, stem + "_media"), dir);
    }

    /// <summary>The .msg as a MIME message: headers, the HTML (or text) body, inline pictures as
    /// cid: parts, and every other attachment, a forwarded mail inside it included (as .msg).
    /// An unsent message (a draft) gets <c>X-Unsent: 1</c>, as an .eml draft would.</summary>
    public static MimeMessage ToMimeMessage(string msgPath)
    {
        using var msg = new OutlookMessage(msgPath, FileAccess.Read);
        var mime = new MimeMessage { Subject = msg.Subject ?? "" };

        if (msg.Sender is { } sender && !string.IsNullOrWhiteSpace(sender.Email ?? sender.DisplayName))
            mime.From.Add(Mailbox(sender.DisplayName, sender.Email));
        foreach (var r in msg.Recipients)
        {
            var list = r.Type switch
            {
                OutlookRecipientType.Cc => mime.Cc,
                OutlookRecipientType.Bcc => mime.Bcc,
                _ => mime.To,
            };
            if (!string.IsNullOrWhiteSpace(r.Email ?? r.DisplayName)) list.Add(Mailbox(r.DisplayName, r.Email));
        }
        // MimeMessage starts with Date = now: a message with no send or receive time must not
        // claim it was sent the moment it was opened.
        if (msg.SentOn is { } sent) mime.Date = sent;
        else if (msg.ReceivedOn is { } received) mime.Date = received;
        else mime.Date = DateTimeOffset.MinValue;
        if (IsUnsent(msgPath)) mime.Headers.Add("X-Unsent", "1");

        var builder = new BodyBuilder();
        var html = msg.BodyHtml;
        if (!string.IsNullOrWhiteSpace(html)) builder.HtmlBody = html;
        builder.TextBody = msg.BodyText ?? "";

        var index = 0;
        foreach (var item in msg.Attachments)
        {
            index++;
            switch (item)
            {
                case OutlookAttachment a when a.Data is { Length: > 0 } data:
                {
                    var name = string.IsNullOrWhiteSpace(a.FileName) ? $"attachment-{index}" : a.FileName;
                    var type = ContentType.Parse(string.IsNullOrWhiteSpace(a.MimeType) ? MimeTypes.GetMimeType(name) : a.MimeType);
                    if (!string.IsNullOrWhiteSpace(a.ContentId) && (a.IsInline || a.Hidden || ReferencedBy(html, a.ContentId)))
                    {
                        var part = (MimePart)builder.LinkedResources.Add(name, data, type);
                        part.ContentId = a.ContentId.Trim('<', '>');
                    }
                    else
                    {
                        builder.Attachments.Add(name, data, type);
                    }
                    break;
                }
                case OutlookMessage inner:
                {
                    // A forwarded mail: keep it whole, as the .msg Outlook can open.
                    using var ms = new MemoryStream();
                    inner.Save(ms);
                    var name = EmailOutbox.SafeStem(string.IsNullOrWhiteSpace(inner.Subject) ? "Attached message" : inner.Subject) + ".msg";
                    builder.Attachments.Add(name, ms.ToArray(), ContentType.Parse("application/vnd.ms-outlook"));
                    break;
                }
            }
        }

        mime.Body = builder.ToMessageBody();
        return mime;
    }

    /// <summary>The subject of a .msg held in memory (the API's "Open in Outlook" names the
    /// outbox file after it).</summary>
    public static string SubjectOf(byte[] msg)
    {
        using var ms = new MemoryStream(msg);
        using var message = new OutlookMessage(ms, FileAccess.Read, false);
        return message.Subject ?? "";
    }

    private static MailboxAddress Mailbox(string? name, string? address)
    {
        var addr = string.IsNullOrWhiteSpace(address) ? "" : address.Trim();
        var display = string.IsNullOrWhiteSpace(name) || name.Trim() == addr ? "" : name.Trim();
        // An Exchange recipient with no SMTP address still has a name worth showing.
        return addr.Contains('@') ? new MailboxAddress(display, addr) : new MailboxAddress(display.Length > 0 ? display : addr, "");
    }

    private static bool ReferencedBy(string? html, string contentId) =>
        html is not null && html.Contains("cid:" + contentId.Trim('<', '>'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the message was never sent (PR_MESSAGE_FLAGS has MSGFLAG_UNSENT): a
    /// draft, which opens without a From/To header block.</summary>
    public static bool IsUnsent(string msgPath)
    {
        try
        {
            using var root = RootStorage.OpenRead(msgPath);
            using var stream = root.OpenStream("__properties_version1.0");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            // Top-level header is 32 bytes, then 16-byte entries: tag, flags, value.
            for (var i = 32; i + 16 <= bytes.Length; i += 16)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x0E070003) continue;
                return (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i + 8)) & 0x08) != 0;
            }
        }
        catch (Exception ex) when (ex is IOException or OpenMcdf.FileFormatException or InvalidDataException or UnauthorizedAccessException) { }
        return false;
    }
}
