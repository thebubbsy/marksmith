using Windows.ApplicationModel.DataTransfer;

namespace MarkSmith.Services;

// One way onto the clipboard for the whole app. Clipboard.SetContent fails outright when another
// process has the clipboard open for a moment (clipboard history, a clipboard manager, a remote
// desktop session syncing it), and the COMException it throws has an empty Message, so the old
// "Clipboard copy failed: " lines told the user nothing. This retries briefly, flushes so the
// text survives MarkSmith closing, and turns a final failure into a sentence a person can act on.
public static class ClipboardWriter
{
    private const int Attempts = 6;

    // Returns null on success, or a user-facing reason on failure.
    public static async Task<string?> TrySetAsync(DataPackage package)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                try { Clipboard.Flush(); } catch { /* still on the clipboard until we close */ }
                return null;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(40 * (attempt + 1));
            }
        }
        return Describe(last);
    }

    // For callers that report failure by catching (the view model's Copy as email): retries the
    // same way, then throws with the readable reason as the message.
    public static void Set(DataPackage package)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                try { Clipboard.Flush(); } catch { /* still on the clipboard until we close */ }
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(40 * (attempt + 1));
            }
        }
        throw new InvalidOperationException(Describe(last), last);
    }

    public static Task<string?> TrySetTextAsync(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        return TrySetAsync(package);
    }

    internal static string Describe(Exception? ex)
    {
        const string busy = "Another app is holding the clipboard. Try again in a moment.";
        if (ex is null) return busy;
        // CLIPBRD_E_CANT_OPEN / CANT_SET: someone else has it open.
        if (ex.HResult is unchecked((int)0x800401D0) or unchecked((int)0x800401D2)) return busy;
        var detail = string.IsNullOrWhiteSpace(ex.Message) ? $"0x{ex.HResult:X8}" : ex.Message.Trim().TrimEnd('.');
        return $"Windows refused the clipboard ({detail}). Try again; if it keeps happening, restart MarkSmith.";
    }
}
