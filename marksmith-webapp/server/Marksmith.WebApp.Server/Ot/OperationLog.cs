namespace MarkSmith.WebApp.Server.Ot;

/// <summary>
/// One sequenced entry in a document's append-only operation log.
/// </summary>
public sealed record LogEntry(
    long Seq,                // 1-based, strictly increasing, assigned by the server
    string ClientId,         // authoring user
    string OpId,             // client-generated operation id
    Operation Op,            // the operation as applied (post-transform)
    DateTimeOffset AppliedAt,
    bool WasNoOp);           // true when the op was sequenced but mutated nothing (satisfied no-op)

/// <summary>
/// The server-side append-only operation log for one document session.
///
/// Responsibilities:
///  * assign sequence numbers in arrival order (the single source of ordering truth),
///  * keep the log for undo/redo and for transforming late-arriving concurrent batches,
///  * support compaction: older entries are folded into document snapshots so the log does not
///    grow unbounded; compaction keeps the log linearizable (see <see cref="SnapshotPoint"/>).
///
/// The log is the recovery record: after a crash, the newest snapshot plus the log entries after
/// it reproduce the exact document state.
/// </summary>
public sealed class OperationLog
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _gate = new();

    public OperationLog(long resumeSeq = 0)
    {
        LastSeq = Math.Max(0, resumeSeq);
        OldestRetainedSeq = resumeSeq + 1; // nothing retained before the resume point
    }

    /// <summary>Highest sequence number assigned so far (0 when empty).</summary>
    public long LastSeq { get; private set; }

    /// <summary>Lowest sequence number still retained in memory (1 when nothing was compacted).</summary>
    public long OldestRetainedSeq { get; private set; } = 1;

    /// <summary>Number of entries currently retained in memory.</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Appends an entry, assigning the next sequence number. Caller must hold the session lock.</summary>
    public LogEntry Append(string clientId, Operation op, bool wasNoOp, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var entry = new LogEntry(++LastSeq, clientId, op.Id, op, at, wasNoOp);
        lock (_gate) _entries.Add(entry);
        return entry;
    }

    /// <summary>All retained entries in sequence order (deep-copied references; entries are immutable).</summary>
    public IReadOnlyList<LogEntry> Entries()
    {
        lock (_gate) return _entries.ToArray();
    }

    /// <summary>Entries with seq &gt; <paramref name="baseSeq"/> -- the concurrent window a new batch must transform against.</summary>
    public IReadOnlyList<LogEntry> After(long baseSeq)
    {
        lock (_gate)
        {
            var start = _entries.FindIndex(e => e.Seq > baseSeq);
            return start < 0 ? Array.Empty<LogEntry>() : _entries.Skip(start).ToArray();
        }
    }

    /// <summary>Entries authored by a client, newest first, down to (and including) <paramref name="uptoSeq"/>.</summary>
    public IReadOnlyList<LogEntry> RecentByClient(string clientId, long uptoSeq)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => e.Seq <= uptoSeq && e.ClientId == clientId)
                .OrderByDescending(e => e.Seq)
                .ToArray();
        }
    }

    /// <summary>Entries authored by a client with seq &gt;= <paramref name="fromSeq"/>, newest first
    /// (undo of "my last batch" — the client sends the first seq of its last acked batch).</summary>
    public IReadOnlyList<LogEntry> RecentByClientAtOrAfter(string clientId, long fromSeq)
    {
        lock (_gate)
        {
            return _entries
                .Where(e => e.Seq >= fromSeq && e.ClientId == clientId)
                .OrderByDescending(e => e.Seq)
                .ToArray();
        }
    }

    /// <summary>
    /// Truncates retained entries to those with seq &gt; <paramref name="snapshotSeq"/>, used after a
    /// snapshot has captured everything up to and including that sequence number. A batch whose
    /// baseSeq falls below the new watermark can no longer be transformed correctly, so callers
    /// must reject it and force a client resync (see DocumentSession.ApplyBatch).
    /// </summary>
    public void CompactTo(long snapshotSeq)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Seq <= snapshotSeq);
            OldestRetainedSeq = snapshotSeq + 1;
        }
    }

    /// <summary>A snapshot point: every op up to <paramref name="Seq"/> is captured in a persisted snapshot.</summary>
    public sealed record SnapshotPoint(long Seq, DateTimeOffset TakenAt);
}

/// <summary>
/// Result of applying one client batch. The batch is atomic: either every operation is sequenced
/// or the whole batch is rejected with <see cref="Error"/>.
/// </summary>
public sealed record BatchResult(
    bool Ok,
    string? Error,                  // rejection reason (atomic failure)
    IReadOnlyList<LogEntry> Entries // sequenced entries, in order (Ok == true)
)
{
    public static BatchResult Reject(string error) => new(false, error, Array.Empty<LogEntry>());
    public static BatchResult Success(IReadOnlyList<LogEntry> entries) => new(true, null, entries);
}
