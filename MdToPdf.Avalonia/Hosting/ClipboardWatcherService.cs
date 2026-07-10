using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace MdToPdf.Avalonia.Hosting;

// Watches the clipboard for Markdown-looking text (copied out of ChatGPT / Gemini / Claude web
// UIs) and hands it to the app — the Avalonia-portable equivalent of MdToPdf/Services/
// ClipboardIngestService.cs, which polls Win32's clipboard sequence number (Windows-only).
// Avalonia's IClipboard has no cross-platform "sequence changed" notification, so this polls
// GetTextAsync directly instead and de-dupes by comparing to the last seen text. Slightly more
// clipboard-API traffic than the WinUI version, same end result.
public sealed class ClipboardWatcherService : IDisposable
{
    private readonly IClipboard _clipboard;
    private readonly DispatcherTimer _timer;
    private readonly Action<string, string, MdToPdf.Models.OutputOverride?> _onIngest;
    private string? _lastSeenText;
    private string? _lastIngestedText;
    private bool _primed;

    public bool IsRunning { get; private set; }

    public ClipboardWatcherService(IClipboard clipboard, Action<string, string, MdToPdf.Models.OutputOverride?> onIngest)
    {
        _clipboard = clipboard;
        _onIngest = onIngest;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public void Start()
    {
        _primed = false; // don't ingest whatever's already on the clipboard when watching turns on
        _timer.Start();
        IsRunning = true;
    }

    public void Stop()
    {
        _timer.Stop();
        IsRunning = false;
    }

    private async Task PollAsync()
    {
        string? text;
        try { text = await _clipboard.TryGetTextAsync(); }
        catch { return; } // clipboard is a shared resource — another process holding it open throws

        if (!_primed) { _lastSeenText = text; _primed = true; return; }
        if (text == _lastSeenText) return;
        _lastSeenText = text;

        if (text is null || !LooksLikeMarkdown(text) || text == _lastIngestedText) return;
        _lastIngestedText = text;

        // The "Copy as Markdown" button also writes an HTML clipboard entry carrying the source
        // page's font (see MdToPdf.Services.ClipboardFontMarker) alongside the plain text above.
        // Avalonia's IClipboard has no fixed "Html" format constant (only Text/Bitmap/File are
        // universal) — discover whichever advertised format looks like HTML instead of guessing
        // the platform's exact native format name.
        var font = await TryGetHtmlFontAsync();
        var output = font is not null ? new MdToPdf.Models.OutputOverride { SourceFontFamily = font } : null;
        _onIngest(text, "clipboard", output);
    }

    private async Task<string?> TryGetHtmlFontAsync()
    {
        try
        {
            var transfer = await _clipboard.TryGetDataAsync();
            if (transfer is null) return null;
            foreach (var item in transfer.Items)
            {
                foreach (var format in item.Formats)
                {
                    if (format.Identifier is not { } id || !id.Contains("html", StringComparison.OrdinalIgnoreCase)) continue;
                    var raw = await item.TryGetRawAsync(format);
                    var html = raw switch
                    {
                        string s => s,
                        byte[] b => System.Text.Encoding.UTF8.GetString(b),
                        _ => null,
                    };
                    var font = MdToPdf.Services.ClipboardFontMarker.Extract(html);
                    if (font is not null) return font;
                }
            }
        }
        catch { /* best-effort — font detection never blocks the actual ingest */ }
        return null;
    }

    // Cheap heuristic: long enough to be a real document and carrying at least one Markdown construct.
    private static bool LooksLikeMarkdown(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Length > 120 &&
        (text.Contains("```") || text.Contains("\n# ") || text.Contains("\n## ") ||
         text.Contains("**") || text.Contains("\n- ") || text.Contains("\n* ") ||
         text.Contains("\n| ") || text.StartsWith("# "));

    public void Dispose() => Stop();
}
