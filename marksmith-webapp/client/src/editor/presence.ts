import type { CaretPosition, SelectionSpan } from "../collab/protocol";

/** A remote user's presence state, as shown by the editor. */
export interface PresencePeer {
  clientId: string;
  color: string;
  caret?: CaretPosition;
  selection?: SelectionSpan;
}

/** Deterministic color per client for avatars/cursors. */
export function colorFor(clientId: string): string {
  let hash = 0;
  for (let i = 0; i < clientId.length; i++) hash = (hash * 31 + clientId.charCodeAt(i)) | 0;
  const hue = Math.abs(hash) % 360;
  return `hsl(${hue} 70% 50%)`;
}
