using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace MdToPdf.Services;

// Watches the clipboard for Markdown-looking text (copied out of ChatGPT / Gemini / Claude web
// UIs) and hands it to the app. Polls Win32's clipboard sequence number instead of subscribing
// to Clipboard.ContentChanged — the event is unreliable in unpackaged WinUI 3 apps, the
// sequence number is not.
public sealed class ClipboardIngestService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private readonly DispatcherQueueTimer _timer;
    private readonly Action<string, string, Models.OutputOverride?> _onIngest;
    private uint _lastSequence;
    private string? _lastIngestedText;

    public bool IsRunning { get; private set; }

    public ClipboardIngestService(DispatcherQueue dispatcherQueue, Action<string, string, Models.OutputOverride?> onIngest)
    {
        _onIngest = onIngest;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(800);
        _timer.IsRepeating = true;
        _timer.Tick += async (_, _) => await PollAsync();
    }

    public void Start()
    {
        _lastSequence = GetClipboardSequenceNumber(); // don't ingest whatever is already there
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
        var seq = GetClipboardSequenceNumber();
        if (seq == _lastSequence) return;
        _lastSequence = seq;

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (!LooksLikeMarkdown(text) || text == _lastIngestedText) return;

            _lastIngestedText = text;

            // The "Copy as Markdown" button also writes an HTML clipboard entry carrying the
            // source page's metadata (font, source, model, title, language/direction, accent —
            // see ClipboardSourceMeta) alongside the plain text above.
            Models.OutputOverride? output = null;
            if (content.Contains(StandardDataFormats.Html))
            {
                try { output = Services.ClipboardSourceMeta.Extract(await content.GetHtmlFormatAsync()); }
                catch { /* HTML format present but unreadable — metadata capture is best-effort */ }
            }
            _onIngest(text, "clipboard", output);
        }
        catch
        {
            // Clipboard is a shared resource — another process holding it open throws. Skip this tick.
        }
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
