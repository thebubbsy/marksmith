namespace MdToPdf.Services;

// WinUI TextBoxes deliver pasted/edited text with bare-CR line endings (a documented XAML quirk);
// clipboard and file sources vary between LF and CRLF. Everything downstream (Markdig, the mermaid
// parsers, fence regexes) assumes LF — a bare-CR document silently collapses into ONE line and
// exports as flattened inline code. Normalize once at every text boundary.
public static class TextNormalizer
{
    public static string Newlines(string s) =>
        string.IsNullOrEmpty(s) ? s : s.Replace("\r\n", "\n").Replace('\r', '\n');
}
