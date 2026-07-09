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
    private readonly Action<string, string> _onIngest;
    private string? _lastSeenText;
    private string? _lastIngestedText;
    private bool _primed;

    public bool IsRunning { get; private set; }

    public ClipboardWatcherService(IClipboard clipboard, Action<string, string> onIngest)
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
        _onIngest(text, "clipboard");
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
