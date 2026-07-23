using Microsoft.UI.Dispatching;

namespace MdToPdf.Services;

// Watches a folder (e.g. Downloads) for new .md/.markdown/.txt files — the "Export" buttons in
// AI chat UIs drop conversation exports there — and hands the file path to the app.
public sealed class FolderIngestService : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action<string> _onFile;
    private FileSystemWatcher? _watcher;

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
        _watcher.Renamed += (s, e) => OnCreated(s, e);
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (ext is not (".md" or ".markdown" or ".txt")) return;

        // Browsers write downloads as .tmp/.crdownload then rename; give the write a moment to finish.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(300);
                try
                {
                    using var _ = File.Open(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    break;
                }
                catch (IOException) { /* still being written */ }
                catch { return; }
            }
            _dispatcherQueue.TryEnqueue(() => _onFile(e.FullPath));
        });
    }

    public void Dispose() => Stop();
}
