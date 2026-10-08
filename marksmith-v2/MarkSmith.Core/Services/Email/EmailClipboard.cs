using System.Text.RegularExpressions;

namespace MarkSmith.Services.Email;

/// <summary>How the pictures in a copied email are referenced.</summary>
public enum ClipboardImageMode
{
    /// <summary><c>data:</c> URIs inside the HTML. New Outlook, Outlook on the web and Gmail keep
    /// them when you paste; nothing has to stay on disk.</summary>
    DataUri,
    /// <summary><c>file:///</c> PNGs in a temp folder. Classic Outlook pastes through Word, which
    /// shows no data: URIs but reads local files and embeds them in the message.</summary>
    File,
}

/// <summary>What "Copy as email" puts on the clipboard: the email-safe HTML body (as pasted into a
/// compose window) and the plain-text body for apps that only take text.</summary>
public sealed record EmailClipboardContent(string Html, string Text, string Subject, IReadOnlyList<string> Notes);

/// <summary>
/// Turns a composed <see cref="EmailDocument"/> into something you can paste into any mail app's
/// compose window. The draft files reference pictures as <c>cid:</c> parts of the message; a
/// clipboard has no parts, so each picture is inlined or written to a temp file instead.
/// </summary>
public static class EmailClipboard
{
    /// <summary>Copied pictures live here until the paste reads them. Old ones are cleared on each
    /// copy; a paste is never a day late.</summary>
    public static string ImageDirectory => Path.Combine(AppPaths.ConfigDir, "clipboard-images");

    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    private static readonly Regex CidSrc = new("src=([\"'])cid:([^\"']+)\\1", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Body = new("<body[^>]*>(.*)</body>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>File paths for classic Outlook (it pastes through Word), data URIs for anything else.</summary>
    public static ClipboardImageMode ModeFor(MailHandler defaultMailApp) =>
        defaultMailApp.Kind == MailAppKind.ClassicOutlook ? ClipboardImageMode.File : ClipboardImageMode.DataUri;

    /// <summary>The pasteable body of <paramref name="doc"/>. With <see cref="ClipboardImageMode.File"/>
    /// the pictures are written to <paramref name="imageDirectory"/> (default
    /// <see cref="ImageDirectory"/>).</summary>
    public static EmailClipboardContent Build(EmailDocument doc, ClipboardImageMode mode, string? imageDirectory = null)
    {
        var html = doc.HtmlBody ?? "";
        var m = Body.Match(html);
        if (m.Success) html = m.Groups[1].Value.Trim();

        var images = doc.InlineImages.ToDictionary(i => i.ContentId, StringComparer.OrdinalIgnoreCase);
        string? dir = null;
        if (mode == ClipboardImageMode.File && images.Count > 0)
        {
            dir = Path.Combine(imageDirectory ?? ImageDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(dir);
        }

        html = CidSrc.Replace(html, match =>
        {
            var quote = match.Groups[1].Value;
            if (!images.TryGetValue(match.Groups[2].Value, out var img)) return match.Value;
            if (mode == ClipboardImageMode.DataUri)
                return $"src={quote}data:{img.MimeType};base64,{Convert.ToBase64String(img.Bytes)}{quote}";
            var path = Path.Combine(dir!, SafeName(img));
            if (!System.IO.File.Exists(path)) System.IO.File.WriteAllBytes(path, img.Bytes);
            return $"src={quote}{new Uri(path).AbsoluteUri}{quote}";
        });

        return new EmailClipboardContent(html, doc.TextBody ?? "", doc.Subject ?? "", doc.Notes.ToList());
    }

    /// <summary>Deletes copied-picture folders older than <see cref="KeepFor"/>.</summary>
    public static int Clean(DateTime? now = null, string? imageDirectory = null)
    {
        var root = imageDirectory ?? ImageDirectory;
        if (!Directory.Exists(root)) return 0;
        var cutoff = (now ?? DateTime.Now) - KeepFor;
        int removed = 0;
        foreach (var d in Directory.GetDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTime(d) < cutoff) { Directory.Delete(d, true); removed++; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    // Named by content id (unique within the message), keeping the picture's own extension.
    private static string SafeName(EmailInlineImage img)
    {
        var ext = Path.GetExtension(img.FileName);
        var name = img.ContentId + (string.IsNullOrEmpty(ext) ? ".png" : ext);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
