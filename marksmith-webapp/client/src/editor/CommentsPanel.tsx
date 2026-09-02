import type { CollabClient } from "../collab/CollabClient";
import type { Operation } from "../collab/protocol";

export interface CommentEntry {
  id: string;
  author: string;
  text: string;
  resolved: boolean;
}

export interface CommentsPanelProps {
  collab: CollabClient;
  comments: CommentEntry[];
}

/**
 * Inline comments panel. v1 comments are anchored to text ranges (server stores them in the
 * OOXML comments part); this panel lists them and supports resolve / un-resolve.
 */
export function CommentsPanel({ collab, comments }: CommentsPanelProps) {
  const resolve = (commentId: string, resolved: boolean) => {
    const o: Operation = {
      id: `cm-${Date.now().toString(36)}`,
      clientId: collab.getClientId(),
      type: "resolveComment",
      commentId,
    };
    collab.submit(o);
    void resolved; // server flips Done; UI reflects after ack
  };

  if (comments.length === 0) {
    return (
      <aside className="ms-panel ms-comments-panel">
        <h3>Comments</h3>
        <p className="ms-panel-empty">No comments yet. Select text and click Comment in the toolbar.</p>
      </aside>
    );
  }

  return (
    <aside className="ms-panel ms-comments-panel">
      <h3>Comments</h3>
      <ul className="ms-comment-list">
        {comments.map((c) => (
          <li key={c.id} className={c.resolved ? "ms-comment resolved" : "ms-comment"}>
            <div className="ms-comment-author">{c.author}</div>
            <div className="ms-comment-text">{c.text}</div>
            <button onClick={() => resolve(c.id, !c.resolved)}>
              {c.resolved ? "Reopen" : "Resolve"}
            </button>
          </li>
        ))}
      </ul>
    </aside>
  );
}
