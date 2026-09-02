import type { Operation } from "./protocol";

/**
 * One-way OT transform — the TypeScript mirror of server/Ot/Transform.cs. Clients use it to
 * rebase their *pending* (un-acked) local operations against remote operations that arrive
 * first, keeping the local DOM in sync with the server's sequencing until the ack arrives.
 *
 * Returns a new operation adjusted as if `prior` had already been applied, or null when the op
 * becomes a satisfied no-op (e.g. deleting a paragraph someone else just deleted).
 */
export function transform(op: Operation, prior: Operation): Operation | null {
  if (prior.type === "insertText") return againstInsert(op, prior);
  if (prior.type === "deleteText") return againstDelete(op, prior);
  if (prior.type === "insertParagraph" || prior.type === "insertTable")
    return againstBlockInsert(op, prior);
  if (prior.type === "deleteParagraph" || prior.type === "deleteTable")
    return againstBlockDelete(op, prior);
  if (prior.type === "insertTableRow" || prior.type === "deleteTableRow")
    return againstTableRow(op, prior);
  if (prior.type === "deleteImage" && op.type === "deleteImage" && op.imageId === prior.imageId)
    return null;
  if (prior.type === "resolveComment" && op.type === "resolveComment" && op.commentId === prior.commentId)
    return null;
  if (
    (prior.type === "acceptTrackChange" || prior.type === "rejectTrackChange") &&
    (op.type === "acceptTrackChange" || op.type === "rejectTrackChange") &&
    op.changeId === prior.changeId
  )
    return null;
  return op;
}

/** Transforms op against a list of priors in order; stops at the first no-op. */
export function transformAgainst(op: Operation, priors: Operation[]): Operation | null {
  let current: Operation | null = op;
  for (const prior of priors) {
    current = transform(current, prior);
    if (current === null) return null;
  }
  return current;
}

function againstInsert(op: Operation, prior: Operation): Operation | null {
  if (isTextOp(op) && op.block === prior.block) {
    const insLen = prior.text?.length ?? 0;
    if ((prior.offset ?? 0) <= (op.offset ?? 0)) {
      return { ...op, offset: (op.offset ?? 0) + insLen };
    }
  }
  return op;
}

function againstDelete(op: Operation, prior: Operation): Operation | null {
  const dStart = prior.offset ?? 0;
  const dLen = prior.length ?? 0;
  const dEnd = dStart + dLen;

  if (isTextOp(op) && op.block === prior.block) {
    const tStart = op.offset ?? 0;
    const tLen = op.type === "insertText" ? op.text?.length ?? 0 : op.length ?? 0;

    if (op.type === "insertText" || op.type === "insertImage" || op.type === "insertHyperlink") {
      if (tStart >= dEnd) return { ...op, offset: tStart - dLen };
      if (tStart >= dStart) return { ...op, offset: dStart };
      return op;
    }

    if (
      op.type === "deleteText" || op.type === "applyFormatting" ||
      op.type === "deleteHyperlink" || op.type === "applyTrackChange"
    ) {
      return transformRangeAgainstDelete(op, dStart, dEnd, tStart, tLen);
    }
  }
  return op;
}

function transformRangeAgainstDelete(
  op: Operation, dStart: number, dEnd: number, tStart: number, tLen: number,
): Operation | null {
  const tEnd = tStart + tLen;

  if (dEnd <= tStart) return { ...op, offset: tStart - (dEnd - dStart) };
  if (dStart >= tEnd) return op;
  if (dStart <= tStart && dEnd >= tEnd) return null;

  const deletedBeforeStart = Math.max(0, Math.min(dEnd, tStart) - dStart);
  const overlapLen = Math.min(tEnd, dEnd) - Math.max(tStart, dStart);
  return { ...op, offset: tStart - deletedBeforeStart, length: tLen - overlapLen };
}

function againstBlockInsert(op: Operation, prior: Operation): Operation | null {
  if (op.block !== undefined && prior.block !== undefined && op.block >= prior.block) {
    return { ...op, block: op.block + 1 };
  }
  return op;
}

function againstBlockDelete(op: Operation, prior: Operation): Operation | null {
  const pb = prior.block ?? 0;

  if (op.type === "insertParagraph" || op.type === "insertTable") {
    if (op.block !== undefined && op.block >= pb + 1) return { ...op, block: op.block - 1 };
    return op;
  }

  if (op.type === "insertTableRow") {
    if (op.block === pb) return null;
    if (op.block !== undefined && op.block >= pb + 1) return { ...op, block: op.block - 1 };
    return op;
  }

  if (op.block !== undefined) {
    if (op.block === pb) {
      const dead = ["deleteText", "applyFormatting", "deleteHyperlink", "applyTrackChange",
        "insertText", "insertImage", "insertHyperlink", "deleteParagraph", "deleteTable"];
      return dead.includes(op.type) ? null : op;
    }
    if (op.block > pb) return { ...op, block: op.block - 1 };
  }
  return op;
}

function againstTableRow(op: Operation, prior: Operation): Operation | null {
  const isInsert = prior.type === "insertTableRow";
  const pRow = prior.row ?? 0;

  if (
    (op.type === "insertTableRow" || op.type === "deleteTableRow") &&
    op.block === prior.block
  ) {
    const r = op.row ?? 0;
    if (isInsert) {
      if (r >= pRow) return { ...op, row: r + 1 };
    } else {
      if (r === pRow) return null;
      if (r > pRow) return { ...op, row: r - 1 };
    }
  }
  return op;
}

function isTextOp(op: Operation): boolean {
  return [
    "insertText", "deleteText", "applyFormatting", "insertImage",
    "insertHyperlink", "deleteHyperlink", "applyTrackChange",
  ].includes(op.type);
}
