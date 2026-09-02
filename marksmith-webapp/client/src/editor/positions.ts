/**
 * DOM <-> OT position mapping for the contenteditable surface.
 *
 * The OT model addresses blocks by index (paragraphs and tables in document order) and character
 * offsets within the block. This module converts between that model and the DOM.
 */

export type BlockEl = HTMLElement;

const BLOCK_TAGS = new Set(["P", "H1", "H2", "H3", "H4", "H5", "H6", "TABLE", "BLOCKQUOTE", "LI"]);

/** All top-level block elements in document order. */
export function blocks(root: HTMLElement): BlockEl[] {
  const out: BlockEl[] = [];
  for (const child of Array.from(root.children)) {
    const el = child as HTMLElement;
    if (BLOCK_TAGS.has(el.tagName)) out.push(el);
  }
  return out;
}

/** Total character length of a block's live text (skips deleted <del> content). */
export function blockTextLength(el: BlockEl): number {
  return liveTextNodes(el).reduce((sum, n) => sum + n.data.length, 0);
}

/** Live text of a block. */
export function blockText(el: BlockEl): string {
  return liveTextNodes(el).map((n) => n.data).join("");
}

function liveTextNodes(el: Element): Text[] {
  const out: Text[] = [];
  const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
  let node = walker.nextNode();
  while (node) {
    // Skip text inside <del> (tracked deletions) and inside comment <mark>? Keep comments visible.
    const parent = node.parentElement;
    if (parent && !parent.closest("del")) out.push(node as Text);
    node = walker.nextNode();
  }
  return out;
}

/** Exported for the DOM applier (normalization). */
export { liveTextNodes };

export interface CaretPos {
  block: number;
  offset: number;
}

/**
 * Computes the OT position of a DOM Range end (or start) within the editor root.
 * Returns null when the range is outside the block list.
 */
export function rangeToPosition(root: HTMLElement, range: Range, atStart: boolean): CaretPos | null {
  const bl = blocks(root);
  const container = range[atStart ? "startContainer" : "endContainer"];
  const offsetInNode = range[atStart ? "startOffset" : "endOffset"];

  // If the container is the block element itself, offset counts child nodes.
  if (container instanceof HTMLElement && BLOCK_TAGS.has(container.tagName)) {
    const idx = bl.indexOf(container as BlockEl);
    if (idx < 0) return null;
    let chars = 0;
    const childNodes = Array.from(container.childNodes);
    for (let i = 0; i < Math.min(offsetInNode, childNodes.length); i++) {
      chars += nodeTextLength(childNodes[i]);
    }
    return { block: idx, offset: chars };
  }

  // Otherwise find the block ancestor and walk text nodes up to the container.
  const blockEl = container.parentElement?.closest(BLOCK_TAGS_SELECTOR) as BlockEl | null;
  if (!blockEl) return null;
  const idx = bl.indexOf(blockEl);
  if (idx < 0) return null;

  let chars = 0;
  for (const tn of liveTextNodes(blockEl)) {
    if (tn === container) {
      chars += Math.min(offsetInNode, tn.data.length);
      return { block: idx, offset: chars };
    }
    chars += tn.data.length;
  }
  // Container is a non-text element inside the block (e.g. <br>): approximate at end of preceding text.
  return { block: idx, offset: chars };
}

function nodeTextLength(node: Node): number {
  if (node.nodeType === Node.TEXT_NODE) return (node as Text).data.length;
  if (node instanceof HTMLElement) {
    if (node.tagName === "DEL") return 0;
    if (node.tagName === "BR") return 1;
    return liveTextNodes(node).reduce((s, n) => s + n.data.length, 0);
  }
  return 0;
}

const BLOCK_TAGS_SELECTOR = "p,h1,h2,h3,h4,h5,h6,table,blockquote,li";

/**
 * Places the selection at an OT position. Returns false when the position cannot be mapped
 * (e.g. after a remote op changed the structure).
 */
export function positionToRange(root: HTMLElement, pos: CaretPos): Range | null {
  const bl = blocks(root);
  if (pos.block < 0 || pos.block >= bl.length) return null;
  const blockEl = bl[pos.block];
  let remaining = pos.offset;

  // Tables: place caret at the start of the first cell's first paragraph (best effort).
  if (blockEl.tagName === "TABLE") {
    const firstP = blockEl.querySelector("p,td");
    if (!firstP) return null;
    return caretRange(firstP, 0);
  }

  for (const tn of liveTextNodes(blockEl)) {
    if (remaining <= tn.data.length) {
      return caretRange(tn, remaining);
    }
    remaining -= tn.data.length;
  }
  // Fallback: end of the block.
  const last = liveTextNodes(blockEl).pop() ?? blockEl;
  return caretRange(last, last instanceof Text ? last.data.length : 0);
}

function caretRange(node: Node, offset: number): Range {
  const range = document.createRange();
  range.setStart(node, Math.min(offset, node instanceof Text ? node.data.length : 1));
  range.collapse(true);
  return range;
}

/** Places the browser selection at an OT position. */
export function selectPosition(root: HTMLElement, pos: CaretPos): void {
  const range = positionToRange(root, pos);
  if (!range) return;
  const sel = window.getSelection();
  sel?.removeAllRanges();
  sel?.addRange(range);
}
