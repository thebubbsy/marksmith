using System.Collections.Concurrent;
using Microsoft.UI.Dispatching;

namespace MdToPdf.Services;

// Watches a folder (e.g. Downloads) for new .md/.markdown/.txt files — the "Export" buttons in
// AI chat UIs drop conversation exports there — and hands the file path to the app.
public sealed class FolderIngestService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action<string> _onFile;
    private FileSystemWatcher? _watcher;

    // Per-path debounce timers (Task 11): an AI tool continuously overwriting a file fires a burst of
    // Created/Changed/Renamed events. We coalesce them per path so exactly one ingest runs, 300ms
    // after the writing settles — instead of racing the writer and throwing IOException.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    public bool IsRunning => _watcher is not null;

    public FolderIngestService(DispatcherQueue dispatcherQueue, Action<string> onFile)
    {
        _dispatcherQueue = dispatcherQueue;
        _onFile = onFile;
    }

    public void Start(string folder)
    {
        Stop();
        if (!Directory.Exists(folder)) return;

        _watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Created += OnCreated;
        _watcher.Changed += OnCreated;
        _watcher.Renamed += (s, e) => OnCreated(s, e);
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        // Cancel any in-flight debounce windows so a stopped watcher doesn't still fire ingests.
        foreach (var kvp in _pending) kvp.Value.Cancel();
        _pending.Clear();
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (ext is not (".md" or ".markdown" or ".txt")) return;

        var path = e.FullPath;

        // Debounce: supersede any pending ingest for this path and restart the quiet window. A file
        // being continuously overwritten keeps resetting the timer until writing pauses for 300ms.
        if (_pending.TryRemove(path, out var previous)) previous.Cancel();
        var cts = new CancellationTokenSource();
        _pending[path] = cts;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DebounceWindow, cts.Token); }
            catch (TaskCanceledException) { return; } // a newer event took over

            // File-lock retry: the writer may still hold the file when the quiet window ends.
            // Browsers also write downloads as .tmp/.crdownload then rename, so poll until readable.
            var readable = false;
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    readable = true;
                    break;
                }
                catch (IOException) { await Task.Delay(300); /* still being written */ }
                catch { break; }
            }

            _pending.TryRemove(path, out _);
            if (!readable) return;
            _dispatcherQueue.TryEnqueue(() => _onFile(path));
        });
    }

    public void Dispose() => Stop();
}

// ISS-011: one-click watch-folder presets for the places local AI agents and CLIs drop their
// Markdown exports. A preset is only offered when its folder (or the parent that would contain
// it) exists, so the picker never suggests paths this machine has never heard of.
public static class AiAgentFolderPresets
{
    public record FolderPreset(string Name, string Description, string Path);

    public static List<FolderPreset> GetAvailablePresets()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return new List<FolderPreset>
        {
            new("Google Antigravity / Gemini CLI", "Default scratch output path for AGY CLI",
                Path.Combine(userProfile, ".gemini", "antigravity", "scratch")),

            new("Ollama Local Models", "Ollama export directory",
                Path.Combine(documents, "Ollama")),

            new("Claude Desktop", "Claude Desktop app exported conversations",
                Path.Combine(appData, "Claude", "Exports")),

            new("GPT-Engineer / Aider CLI", "CLI coding agent workspace output",
                Path.Combine(userProfile, ".local", "share", "gpt-engineer")),

            new("Downloads Folder", "Browser downloaded AI markdown files",
                Path.Combine(userProfile, "Downloads"))
        }.Where(p => Directory.Exists(p.Path) || Directory.Exists(Path.GetDirectoryName(p.Path))).ToList();
    }
}
