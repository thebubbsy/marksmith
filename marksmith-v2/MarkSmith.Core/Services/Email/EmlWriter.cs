using MimeKit;
using MimeKit.Utils;

namespace MarkSmith.Services.Email;

/// <summary>Writes an <see cref="EmailDocument"/> as a standard .eml (RFC 5322 / MIME) file:
/// multipart/mixed [ multipart/alternative [ text/plain, multipart/related [ text/html, inline
/// PNGs ] ], attachments ]. Every mail app opens it; with <c>X-Unsent: 1</c> Outlook opens it as
/// a draft you can edit and send.</summary>
public static class EmlWriter
{
    public static MimeMessage ToMimeMessage(EmailDocument doc)
    {
        var msg = new MimeMessage
        {
            Subject = doc.Subject ?? "",
            Date = DateTimeOffset.Now,
            // MimeKit's default Message-ID carries the machine's host name; ours doesn't.
            MessageId = MimeUtils.GenerateMessageId("marksmith.local"),
        };
        if (!string.IsNullOrWhiteSpace(doc.From)) AddAll(msg.From, new[] { doc.From });
        AddAll(msg.To, doc.To);
        AddAll(msg.Cc, doc.Cc);
        AddAll(msg.Bcc, doc.Bcc);
        if (doc.IsDraft) msg.Headers.Add("X-Unsent", "1");

        var builder = new BodyBuilder
        {
            TextBody = doc.TextBody,
            HtmlBody = doc.HtmlBody,
        };
        foreach (var img in doc.InlineImages)
        {
            var part = (MimePart)builder.LinkedResources.Add(img.FileName, img.Bytes, ContentType.Parse(img.MimeType));
            part.ContentId = img.ContentId;
            part.ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = img.FileName };
        }
        foreach (var att in doc.Attachments)
            builder.Attachments.Add(att.FileName, att.Bytes, ContentType.Parse(att.MimeType));
        msg.Body = builder.ToMessageBody();
        return msg;
    }

    public static byte[] ToBytes(EmailDocument doc)
    {
        using var ms = new MemoryStream();
        ToMimeMessage(doc).WriteTo(ms);
        return ms.ToArray();
    }

    public static void Write(EmailDocument doc, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        try
        {
            using (var fs = File.Create(tmp)) ToMimeMessage(doc).WriteTo(fs);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            // A write that failed half way leaves no stray .tmp beside the user's documents.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static void AddAll(InternetAddressList list, IEnumerable<string> addresses)
    {
        foreach (var a in addresses)
            if (MailboxAddress.TryParse(a, out var mb)) list.Add(mb);
    }
}
