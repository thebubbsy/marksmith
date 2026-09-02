import type { CollabClient } from "../collab/CollabClient";
import type { Operation } from "../collab/protocol";

export interface TrackChangeEntry {
  id: string;
  author: string;
  kind: "insert" | "delete" | "format";
  changeId: string;
}

export interface TrackChangesPanelProps {
  collab: CollabClient;
  changes: TrackChangeEntry[];
}

/**
 * Track changes panel: lists in-document change markers (server renders them as
 * data-ms-change elements) and offers accept / reject per change id. The server decides the
 * winner when two users decide the same change concurrently (first sequenced wins; the loser is
 * a satisfied no-op).
 */
export function TrackChangesPanel({ collab, changes }: TrackChangesPanelProps) {
  const decide = (changeId: string, accept: boolean) => {
    const o: Operation = {
      id: `tc-${Date.now().toString(36)}`,
      clientId: collab.getClientId(),
      type: accept ? "acceptTrackChange" : "rejectTrackChange",
      changeId,
    };
    collab.submit(o);
  };

  if (changes.length === 0) {
    return (
      <aside className="ms-panel ms-track-panel">
        <h3>Track Changes</h3>
        <p className="ms-panel-empty">No tracked changes. Toggle Suggestions and edit to create some.</p>
      </aside>
    );
  }

  return (
    <aside className="ms-panel ms-track-panel">
      <h3>Track Changes</h3>
      <ul className="ms-change-list">
        {changes.map((c, i) => (
          <li key={`${c.changeId}-${i}`} className={`ms-change ${c.kind}`}>
            <span className="ms-change-meta">
              <b>{c.kind}</b> by {c.author}
            </span>
            <span className="ms-change-actions">
              <button onClick={() => decide(c.changeId, true)}>Accept</button>
              <button onClick={() => decide(c.changeId, false)}>Reject</button>
            </span>
          </li>
        ))}
      </ul>
    </aside>
  );
}
