import type { Operation } from "../collab/protocol";
import { blocks, blockTextLength, selectPosition, positionToRange, type BlockEl } from "./positions";

/**
 * Applies operations to the contenteditable DOM. This is the client mirror of the server's
 * OpApplier (server/Documents/OpApplier.cs): local edits are applied optimistically, and remote
 * ops from the server are applied here too so every client converges on the same DOM.
 *
 * v1 scope: text, formatting and block-level ops are mirrored exactly. Ops the DOM cannot mirror
 * faithfully (images, comments, track changes, hyperlinks) are applied by re-rendering the
 * server HTML after the next resync; the caller decides when to resync (see EditorSurface).
 */

export interface DomApplyResult {
  applied: boolean;
  /** When false, the caller should request a resync from the server. */
  needsResync: boolean;
}

export function applyOp(root: HTMLElement, op: Operation, fallbackToResync: (op: Operation) => void): DomApplyResult {
  switch (op.type) {
    case "insertText": return insertText(root, op);
    case "deleteText": return deleteText(root, op);
    case "applyFormatting": return applyFormatting(root, op);
    case "insertParagraph": return insertParagraph(root, op);
    case "deleteParagraph": return deleteParagraph(root, op);
    default:
      // Structural/rich ops (tables, images, comments, track changes): the server re-renders
      // full HTML; the client requests a resync to pick it up.
      fallbackToResync(op);
      return { applied: false, needsResync: true };
  }
}

// ------------------------------------------------------------------ text

function insertText(root: HTMLElement, op: Operation): DomApplyResult {
  const bl = blocks(root);
  const block = bl[op.block ?? -1];
  if (!block || block.tagName === "TABLE") return { applied: false, needsResync: true };
  const offset = op.offset ?? 0;
  const text = op.text ?? "";
  if (offset < 0 || offset > blockTextLength(block)) return { applied: false, needsResync: true };

  const range = positionToRange(root, { block: op.block!, offset });
  if (!range) return { applied: false, needsResync: true };

  range.insertNode(document.createTextNode(text));
  return { applied: true, needsResync: false };
}

function deleteText(root: HTMLElement, op: Operation): DomApplyResult {
  const bl = blocks(root);
  const block = bl[op.block ?? -1];
  if (!block || block.tagName === "TABLE") return { applied: false, needsResync: true };
  const offset = op.offset ?? 0;
  const length = op.length ?? 0;
  if (offset < 0 || offset + length > blockTextLength(block)) return { applied: false, needsResync: true };

  const start = positionToRange(root, { block: op.block!, offset });
  if (!start) return { applied: false, needsResync: true };
  const end = positionToRange(root, { block: op.block!, offset: offset + length });
  if (!end) return { applied: false, needsResync: true };

  const range = document.createRange();
  range.setStart(start.startContainer, start.startOffset);
  range.setEnd(end.startContainer, end.startOffset);
  range.deleteContents();
  // Merge adjacent text nodes that the split may have created.
  normalizeBlock(block);
  return { applied: true, needsResync: false };
}

// ------------------------------------------------------------------ formatting

function applyFormatting(root: HTMLElement, op: Operation): DomApplyResult {
  const bl = blocks(root);
  const block = bl[op.block ?? -1];
  if (!block || block.tagName === "TABLE") return { applied: false, needsResync: true };
  const offset = op.offset ?? 0;
  const length = op.length ?? 0;
  const len = blockTextLength(block);
  if (offset < 0 || offset + length > len) return { applied: false, needsResync: true };
  if (length === 0) return { applied: true, needsResync: false };

  const start = positionToRange(root, { block: op.block!, offset });
  const end = positionToRange(root, { block: op.block!, offset: offset + length });
  if (!start || !end) return { applied: false, needsResync: true };

  const range = document.createRange();
  range.setStart(start.startContainer, start.startOffset);
  range.setEnd(end.startContainer, end.startOffset);

  const f = op.format;
  const style: Record<string, string> = {};
  if (f?.bold) style["font-weight"] = "700";
  if (f?.italic) style["font-style"] = "italic";
  if (f?.underline) style["text-decoration"] = "underline";
  if (f?.strikethrough) style["text-decoration"] = "line-through";
  if (f?.color) style["color"] = f.color;

  // v1: wrap the range in a span carrying the format. (No unbolding in v1's DOM mirror; the
  // server HTML is authoritative and a resync reconciles.)
  if (Object.keys(style).length === 0) return { applied: true, needsResync: false };

  const span = document.createElement("span");
  span.style.cssText = Object.entries(style).map(([k, v]) => `${k}:${v}`).join(";");
  try {
    range.surroundContents(span);
  } catch {
    return { applied: false, needsResync: true };
  }
  return { applied: true, needsResync: false };
}

// ------------------------------------------------------------------ blocks

function insertParagraph(root: HTMLElement, op: Operation): DomApplyResult {
  const bl = blocks(root);
  const index = op.block ?? bl.length;
  if (index < 0 || index > bl.length) return { applied: false, needsResync: true };

  const tag = (op.style ?? "Normal").startsWith("Heading") ? (op.style as string).toLowerCase() : "p";
  const el = document.createElement(tag);
  el.textContent = "";

  if (index >= bl.length) root.appendChild(el);
  else bl[index].insertAdjacentElement("beforebegin", el);
  return { applied: true, needsResync: false };
}

function deleteParagraph(root: HTMLElement, op: Operation): DomApplyResult {
  const bl = blocks(root);
  const index = op.block ?? -1;
  if (index < 0 || index >= bl.length) return { applied: false, needsResync: true };
  bl[index].remove();
  return { applied: true, needsResync: false };
}

// ------------------------------------------------------------------ helpers

function normalizeBlock(block: BlockEl): void {
  // Merge adjacent text nodes so offsets stay stable.
  const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT);
  let prev: Text | null = null;
  let node = walker.nextNode() as Text | null;
  while (node) {
    if (prev && node.previousSibling === prev) {
      prev.data += node.data;
      node.remove();
    } else {
      prev = node;
    }
    node = walker.nextNode() as Text | null;
  }
}

/** Restores the caret after applying a batch (best-effort; returns false when the position is gone). */
export function restoreCaret(root: HTMLElement, block: number, offset: number): boolean {
  if (block < 0) return false;
  try {
    selectPosition(root, { block, offset });
    return true;
  } catch {
    return false;
  }
}
