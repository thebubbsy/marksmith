using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarkSmith.Services;

/// <summary>One undo step: the full document text plus the caret position to restore.</summary>
public sealed class UndoSnapshot
{
    public string Text { get; set; } = "";
    public int Caret { get; set; }
}

/// <summary>Persistent per-document undo/redo stacks for the markdown editor.
///
/// Design notes:
///  - The editor's native WinUI undo is in-memory only and dies on restart, so the app owns a
///    snapshot stack instead (same model as the Mermaid studio, but persisted to disk).
///  - Consecutive typing within a short window coalesces into ONE undo step (a burst); a pause or
///    an explicit <see cref="BreakBurst"/>() starts a fresh step. The step stores the text BEFORE
///    the burst so Ctrl+Z returns exactly to where the burst began.
///  - Stacks are keyed by document path ("" = untitled paste document) and written to
///    undo_history.json on document switch and on <see cref="Flush"/>() (app exit), so Ctrl+Z
///    keeps working after the app is closed and re-opened.
///  - Steps are capped per document (count + bytes) so a huge document cannot balloon memory.
///  - Every public member is thread-safe: the file-open path records changes from a background
///    thread.
/// </summary>
public sealed class EditorUndoHistory
{
    private const string StoreFileName = "undo_history.json";
    private const int MaxStepsPerDoc = 120;
    private const int MaxDocs = 16;
    private const long MaxBytesPerDoc = 8 * 1024 * 1024; // ~8 MB of text per document
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(1500);

    private readonly object _gate = new();
    private readonly string _storePath;
    private readonly Dictionary<string, StackState> _byDoc = new(StringComparer.OrdinalIgnoreCase);
    private StackState _current = new();
    private string _currentKey = "";
    private readonly JsonSerializerOptions _json = new();

    private sealed class StackState
    {
        public List<UndoSnapshot> Undo = new();
        public List<UndoSnapshot> Redo = new();
        public long LastPushTicks; // when the current burst last received a change
        public string LastText = ""; // the editor's text at the last recorded boundary
        public int LastCaret;
        public long TotalBytes;
        public long LastUsedTicks; // LRU eviction across documents
    }

    private sealed class DocState
    {
        public List<UndoSnapshot>? Undo { get; set; }
        public List<UndoSnapshot>? Redo { get; set; }
        public string LastText { get; set; } = "";
        public int LastCaret { get; set; }
    }

    public EditorUndoHistory(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(AppPaths.ConfigDir, StoreFileName);
        Load();
    }

    public bool CanUndo { get { lock (_gate) return _current.Undo.Count > 0; } }
    public bool CanRedo { get { lock (_gate) return _current.Redo.Count > 0; } }

    /// <summary>Switches the active document ("" = untitled). Does not touch the content.</summary>
    public void SetDocument(string? path)
    {
        lock (_gate)
        {
            string key = NormalizeKey(path);
            if (key == _currentKey) return;
            _current = StateForLocked(key);
            _currentKey = key;
            // Persist the stacks we are leaving behind right away, so a crash after switching
            // documents does not lose the previous document's undo history.
            FlushLocked();
        }
    }

    /// <summary>Switches documents and seeds the current text WITHOUT recording an undo step.
    /// Used when a file is opened: the loaded content must not become an undoable change.</summary>
    public void Seed(string? path, string text, int caret = 0)
    {
        lock (_gate)
        {
            _current = StateForLocked(NormalizeKey(path));
            _currentKey = NormalizeKey(path);
            _current.LastText = text ?? "";
            _current.LastCaret = caret;
            _current.LastPushTicks = 0;
        }
    }

    /// <summary>Called on every editor change. Coalesces a typing burst into one undo step and
    /// clears redo (a new edit invalidates it).</summary>
    public void RecordChange(string text, int caret)
    {
        lock (_gate)
        {
            var s = _current;
            string t = text ?? "";
            if (t == s.LastText) return; // no real change (e.g. the binding round-trip after undo)

            long now = DateTime.UtcNow.Ticks;
            if (s.Redo.Count > 0) s.Redo.Clear();

            bool continuing = s.Undo.Count > 0 && (now - s.LastPushTicks) < CoalesceWindow.Ticks;
            if (!continuing)
            {
                // Open a new step holding the state BEFORE this burst.
                PushLocked(s, new UndoSnapshot { Text = s.LastText, Caret = s.LastCaret });
            }
            s.LastPushTicks = now;
            s.LastText = t;
            s.LastCaret = caret;
        }
    }

    /// <summary>Forces the next <see cref="RecordChange"/> to open a fresh undo step regardless of
    /// timing. Call BEFORE a programmatic content injection (portal edit, Replace All, restore,
    /// mermaid sync-back) so that injection becomes undoable on its own.</summary>
    public void BreakBurst()
    {
        lock (_gate) _current.LastPushTicks = 0;
    }

    /// <summary>Pops the last undo step (or returns null). The caller applies the text; the state
    /// bookkeeping is done here so the follow-up binding round-trip is deduped.</summary>
    public UndoSnapshot? Undo()
    {
        lock (_gate)
        {
            var s = _current;
            if (s.Undo.Count == 0) return null;
            var snap = s.Undo[^1];
            s.Undo.RemoveAt(s.Undo.Count - 1);
            s.TotalBytes -= SnapBytes(snap);
            PushRedoLocked(s, new UndoSnapshot { Text = s.LastText, Caret = s.LastCaret });
            s.LastText = snap.Text;
            s.LastCaret = snap.Caret;
            s.LastPushTicks = 0; // the next edit after an undo opens a fresh step
            return snap;
        }
    }

    public UndoSnapshot? Redo()
    {
        lock (_gate)
        {
            var s = _current;
            if (s.Redo.Count == 0) return null;
            var snap = s.Redo[^1];
            s.Redo.RemoveAt(s.Redo.Count - 1);
            PushLocked(s, new UndoSnapshot { Text = s.LastText, Caret = s.LastCaret });
            s.LastText = snap.Text;
            s.LastCaret = snap.Caret;
            s.LastPushTicks = 0;
            return snap;
        }
    }

    /// <summary>Writes all documents' stacks to disk (atomic tmp + move). Called on document
    /// switch and on app exit so undo survives close/reopen.</summary>
    public void Flush()
    {
        lock (_gate) FlushLocked();
    }

    private void FlushLocked()
    {
        var data = new Dictionary<string, DocState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, s) in _byDoc)
        {
            data[key] = new DocState
            {
                Undo = s.Undo,
                Redo = s.Redo,
                LastText = s.LastText,
                LastCaret = s.LastCaret,
            };
        }

        string? tmp = null;
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            tmp = _storePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, _json));
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch
        {
            try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ internals

    private static string NormalizeKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path); } catch { return path.Trim(); }
    }

    private StackState StateForLocked(string key)
    {
        if (!_byDoc.TryGetValue(key, out var state))
        {
            state = new StackState { LastUsedTicks = DateTime.UtcNow.Ticks };
            _byDoc[key] = state;
            // Bound the store: evict the least-recently-used document (never the fresh one).
            if (_byDoc.Count > MaxDocs)
            {
                var stale = _byDoc
                    .Where(kv => kv.Key != key)
                    .OrderBy(kv => kv.Value.LastUsedTicks)
                    .FirstOrDefault();
                if (stale.Key != null)
                {
                    FlushLocked(); // persist everything (incl. the evicted doc) before dropping it
                    _byDoc.Remove(stale.Key);
                }
            }
        }
        state.LastUsedTicks = DateTime.UtcNow.Ticks;
        return state;
    }

    private static long SnapBytes(UndoSnapshot s) => s.Text.Length * 2L;

    private static void PushLocked(StackState s, UndoSnapshot snap)
    {
        s.Undo.Add(snap);
        s.TotalBytes += SnapBytes(snap);
        while (s.Undo.Count > MaxStepsPerDoc || s.TotalBytes > MaxBytesPerDoc)
        {
            s.TotalBytes -= SnapBytes(s.Undo[0]);
            s.Undo.RemoveAt(0);
        }
    }

    private static void PushRedoLocked(StackState s, UndoSnapshot snap)
    {
        s.Redo.Add(snap);
        while (s.Redo.Count > MaxStepsPerDoc) s.Redo.RemoveAt(0);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            var json = File.ReadAllText(_storePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, DocState>>(json, _json);
            if (data == null) return;
            foreach (var (key, doc) in data)
            {
                if (string.IsNullOrEmpty(key) || _byDoc.ContainsKey(key)) continue;
                var state = new StackState
                {
                    Undo = doc.Undo ?? new List<UndoSnapshot>(),
                    Redo = doc.Redo ?? new List<UndoSnapshot>(),
                    LastText = doc.LastText,
                    LastCaret = doc.LastCaret,
                    TotalBytes = (doc.Undo ?? new List<UndoSnapshot>()).Sum(SnapBytes),
                    LastUsedTicks = DateTime.UtcNow.Ticks,
                };
                _byDoc[key] = state;
            }
        }
        catch { /* corrupt/absent store — start fresh */ }
    }
}
