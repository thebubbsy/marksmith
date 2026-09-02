import { useEffect, useRef, useCallback } from "react";
import type { CollabClient } from "../collab/CollabClient";
import type { CaretPosition, Operation, SelectionSpan } from "../collab/protocol";
import { rangeToPosition } from "./positions";
import { applyOp, restoreCaret } from "./domApplier";
import type { PresencePeer } from "./presence";

export interface EditorSurfaceProps {
  collab: CollabClient;
  initialHtml: string;
  presence: PresencePeer[];
  suggestionMode: boolean;
  onCaretChange?: (caret: CaretPosition, selection?: SelectionSpan) => void;
  onUserOp?: (op: Operation) => void;
  zoom?: number;
  fontFamily?: string;
}

let opCounter = 0;

function newOp(type: Operation["type"]): Operation {
  opCounter++;
  return { id: `op-${Date.now().toString(36)}-${opCounter}`, clientId: "", type };
}

/**
 * The WYSIWYG editing surface: a contenteditable element that mirrors the server's block model.
 *
 * Editing model (v1):
 *  * The DOM is the local view; the server's OOXML is authoritative.
 *  * Every user edit is converted into operations (insertText / deleteText / applyFormatting /
 *    insertParagraph / deleteParagraph), applied optimistically here, then batched to the server.
 *  * Remote ops from the server are applied through the same DOM applier, so all clients converge
 *    on the same DOM between server re-renders.
 *  * Structural/rich ops (tables, images, comments, track changes) trigger a resync so the
 *    server's authoritative HTML replaces the local DOM.
 */
export function EditorSurface(props: EditorSurfaceProps) {
  const { collab, initialHtml, suggestionMode, onCaretChange, onUserOp, zoom = 100, fontFamily = "sans" } = props;
  const rootRef = useRef<HTMLDivElement>(null);
  const caretTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastCaret = useRef<CaretPosition>({ block: 0, offset: 0 });

  // Bootstrap: install the server HTML once.
  useEffect(() => {
    if (rootRef.current) rootRef.current.innerHTML = initialHtml;
  }, [initialHtml]);

  // ---- op submission (optimistic apply + send) ----
  const submitOp = useCallback(
    (op: Operation) => {
      op.clientId = collab.getClientId() || "local";
      onUserOp?.(op);
      // Optimistic local apply for text ops.
      const root = rootRef.current;
      if (root && (op.type === "insertText" || op.type === "deleteText")) {
        applyOp(root, op, () => collab.resync());
      }
      collab.submit(op);
    },
    [collab, onUserOp],
  );

  // ---- input handling: convert DOM edits to ops ----
  const handleBeforeInput = useCallback(
    (e: React.FormEvent<HTMLDivElement>) => {
      const input = e.nativeEvent as InputEvent;
      const root = rootRef.current;
      if (!root) return;
      const sel = window.getSelection();
      if (!sel || sel.rangeCount === 0) return;

      // Skip composition (IME) — handled by handleInput instead.
      if (input.isComposing || input.inputType?.startsWith("insertComposition")) return;

      const range = sel.getRangeAt(0);
      const caret = rangeToPosition(root, range, true);
      if (!caret) return;

      switch (input.inputType) {
        case "insertText": {
          const text = input.data ?? "";
          if (!text) break;
          e.preventDefault();
          const op = newOp("insertText");
          op.block = caret.block;
          op.offset = caret.offset;
          op.text = text;
          submitOp(op);
          break;
        }
        case "deleteContentBackward": {
          e.preventDefault();
          if (!range.collapsed) {
            const end = rangeToPosition(root, range, false);
            if (!end) break;
            const op = newOp("deleteText");
            op.block = caret.block;
            op.offset = end.offset;
            op.length = caret.offset - end.offset;
            submitOp(op);
            break;
          }
          if (caret.offset > 0) {
            const op = newOp("deleteText");
            op.block = caret.block;
            op.offset = caret.offset - 1;
            op.length = 1;
            submitOp(op);
          } else if (caret.block > 0) {
            // Merge with the previous block: delete the paragraph break (v1: delete prev block).
            const op = newOp("deleteParagraph");
            op.block = caret.block - 1;
            submitOp(op);
          }
          break;
        }
        case "deleteContentForward": {
          e.preventDefault();
          if (!range.collapsed) {
            const end = rangeToPosition(root, range, false);
            if (!end) break;
            const op = newOp("deleteText");
            op.block = caret.block;
            op.offset = caret.offset;
            op.length = end.offset - caret.offset;
            submitOp(op);
            break;
          }
          const op = newOp("deleteText");
          op.block = caret.block;
          op.offset = caret.offset;
          op.length = 1;
          submitOp(op);
          break;
        }
        case "insertParagraph": {
          e.preventDefault();
          const op = newOp("insertParagraph");
          op.block = caret.block + 1;
          op.style = "Normal";
          submitOp(op);
          break;
        }
      }
    },
    [submitOp],
  );

  // IME/insertCompositionText: simplest v1 behavior — insert the committed text as one op.
  const handleInput = useCallback(
    (e: React.FormEvent<HTMLDivElement>) => {
      const input = e.nativeEvent as InputEvent;
      if (!input.isComposing && input.data && input.inputType === "insertCompositionText") {
        const root = rootRef.current;
        if (!root) return;
        const sel = window.getSelection();
        if (!sel || sel.rangeCount === 0) return;
        const caret = rangeToPosition(root, sel.getRangeAt(0), true);
        if (!caret) return;
        const op = newOp("insertText");
        op.block = caret.block;
        op.offset = caret.offset;
        op.text = input.data;
        submitOp(op);
      }
    },
    [submitOp],
  );

  // ---- caret tracking (presence) ----
  const trackCaret = useCallback(() => {
    const root = rootRef.current;
    if (!root) return;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return;
    const range = sel.getRangeAt(0);
    const caret = rangeToPosition(root, range, true);
    if (!caret) return;
    lastCaret.current = caret;
    if (caretTimer.current) clearTimeout(caretTimer.current);
    caretTimer.current = setTimeout(() => {
      // Throttle presence to ~200ms, matching the op batch cadence.
      const end = rangeToPosition(root, range, false);
      const selection = !range.collapsed && end ? { start: caret, end } : undefined;
      onCaretChange?.(caret, selection);
    }, 200);
  }, [onCaretChange]);

  // ---- remote ops: apply via the DOM applier, preserve the caret ----
  useEffect(() => {
    collab.setRemoteOpsApplier((entries) => {
      const root = rootRef.current;
      if (!root) return;
      for (const entry of entries) {
        applyOp(root, entry.op, () => collab.resync());
      }
      // Best-effort caret preservation after remote changes.
      restoreCaret(root, lastCaret.current.block, lastCaret.current.offset);
    });
    return () => collab.setRemoteOpsApplier(null);
  }, [collab]);

  const fontStyle =
    fontFamily === "serif"
      ? "var(--ms-font-serif)"
      : fontFamily === "mono"
      ? "var(--ms-font-mono)"
      : "var(--ms-font-sans)";

  return (
    <div
      ref={rootRef}
      className="ms-editor-surface ms-page-canvas"
      contentEditable
      suppressContentEditableWarning
      onBeforeInput={handleBeforeInput}
      onInput={handleInput}
      onMouseUp={trackCaret}
      onKeyUp={trackCaret}
      onBlur={trackCaret}
      data-suggestion-mode={suggestionMode ? "true" : "false"}
      style={{
        transform: zoom !== 100 ? `scale(${zoom / 100})` : undefined,
        transformOrigin: "top center",
        fontFamily: fontStyle,
      }}
    />
  );
}

/** Renders remote cursors as positioned overlays (v1: simplest form — fixed side rail). */
export function PresenceRail({ peers }: { peers: PresencePeer[] }) {
  if (peers.length === 0) return null;
  return (
    <div className="ms-presence-rail">
      {peers.map((p) => (
        <span key={p.clientId} className="ms-presence-badge" style={{ background: p.color }}>
          {p.clientId}
          {p.caret ? ` @${p.caret.block}:${p.caret.offset}` : ""}
        </span>
      ))}
    </div>
  );
}
