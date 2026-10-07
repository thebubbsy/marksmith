namespace MarkSmith.Services.Email;

/// <summary>An image the HTML body references as <c>cid:ContentId</c>. Classic Outlook renders mail
/// with Word's engine, which shows neither SVG nor data: URIs, so every picture in an email goes
/// out as an inline PNG part instead.</summary>
public sealed record EmailInlineImage(string ContentId, byte[] Bytes, string MimeType, string FileName, int Width, int Height);

/// <summary>A regular attachment (a PDF copy of the document, say).</summary>
public sealed record EmailAttachment(string FileName, byte[] Bytes, string MimeType);

/// <summary>Everything an .eml / .msg writer needs. Built by <see cref="EmailComposer"/>; the writers
/// never touch Markdown.</summary>
public sealed class EmailDocument
{
    public string Subject { get; set; } = "";
    public List<string> To { get; } = new();
    public List<string> Cc { get; } = new();
    public List<string> Bcc { get; } = new();
    public string HtmlBody { get; set; } = "";
    public string TextBody { get; set; } = "";
    public List<EmailInlineImage> InlineImages { get; } = new();
    public List<EmailAttachment> Attachments { get; } = new();

    /// <summary>True writes the draft flag (<c>X-Unsent: 1</c>), so Outlook opens the file as a
    /// message you can edit and send rather than one you received.</summary>
    public bool IsDraft { get; set; } = true;

    /// <summary>Notes the composer wants the user to see (a DOCX attachment skipped on the free
    /// plan, a diagram that couldn't be drawn). Never written into the email itself.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Total size of every part, before MIME encoding. New Outlook refuses .msg files over
    /// 14 MB, and most mail servers cap messages at 20–25 MB after base64 (×4/3).</summary>
    public long PayloadBytes =>
        System.Text.Encoding.UTF8.GetByteCount(HtmlBody) + System.Text.Encoding.UTF8.GetByteCount(TextBody)
        + InlineImages.Sum(i => (long)i.Bytes.Length) + Attachments.Sum(a => (long)a.Bytes.Length);
}
