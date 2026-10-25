namespace MarkSmith.Services;

// Text the app puts on the clipboard. The editor's TextBox stores each line break as a bare '\r',
// so copies taken from it (Copy Markdown, a copied selection, the status-bar extracts) reached
// other apps with old Mac breaks, and some pasted them as one long line. Windows apps expect CRLF,
// which is also what the TextBox's own Ctrl+C puts there.
public static class ClipboardText
{
    public static string WindowsLineBreaks(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0) return text;

        var sb = new System.Text.StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                sb.Append("\r\n");
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n')
            {
                sb.Append("\r\n");
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
