# OT Specification — Marksmith.WebApp v1

> Deliverable 02 of 12. This spec is the authoritative description of the transformation logic;
> the implementation lives in `server/Marksmith.WebApp.Server/Ot/` (C#) and is mirrored in
> `client/src/collab/transform.ts` (TypeScript). Both sides are exercised by
> `server/Marksmith.WebApp.Tests/TransformTests.cs`.

## 1. Model

The document is a **list of blocks** in body order. A block is either a **paragraph** or a
**table**. Blocks are addressed by a 0-based index (`block`).

Text-level operations additionally address a character **offset** inside the block's live text
and (for ranges) a **length**:

```
block: int      // index into the body block list
offset: int     // 0-based char offset inside the block
length: int     // range length in chars (delete/format ops)
```

Block-level operations (insert/delete paragraph/table, table rows) address only `block` (and
`row` for rows). ID-based operations (images, comments, track changes) reference stable ids and
carry no position.

## 2. Operation types (v1)

| Op | Fields | Semantics |
|---|---|---|
| `insertText` | block, offset, text | insert `text` at `(block, offset)` |
| `deleteText` | block, offset, length | remove `length` chars at `(block, offset)` |
| `applyFormatting` | block, offset, length, format | set bold/italic/underline/strikethrough/color on the range |
| `insertParagraph` | block, style | insert a new paragraph **before** `block` with `style` (Normal/H1–H6) |
| `deleteParagraph` | block | remove the paragraph at `block` |
| `insertTable` | block, rows, cols | insert a table before `block` |
| `deleteTable` | block | remove the table at `block` |
| `insertTableRow` | block, row | insert a row at `row` inside the table at `block` |
| `deleteTableRow` | block, row | remove the row at `row` (last row is protected) |
| `insertImage` | block, offset, alt, dataUri, width, height | insert an inline image (data URI; server embeds it) |
| `deleteImage` | imageId | remove the image by id |
| `insertHyperlink` | block, offset, text, url | insert a hyperlink run |
| `deleteHyperlink` | block, offset, length | remove hyperlink runs overlapping the range |
| `addComment` | commentId, block, offset, length, author, text | anchor a comment to the range |
| `resolveComment` | commentId | mark the comment done |
| `applyTrackChange` | changeId, block, offset, length, kind, author | mark range as insert/delete/format change |
| `acceptTrackChange` | changeId | accept the change (keep insert, drop delete) |
| `rejectTrackChange` | changeId | reject the change (drop insert, keep delete) |

Meta operations (never document ops):

| Op | Fields | Semantics |
|---|---|---|
| `undo` | uptoSeq | client asks the server to inverse its own ops down to `uptoSeq` |
| `redo` | fromSeq | v1: no redo stack; the client re-sends the forward op |

## 3. Sequencing

* The server assigns each entry a strictly increasing `seq` in arrival order (`OperationLog`).
* A client's batch carries `baseSeq` — the highest seq the client's view already includes.
* Incoming ops are transformed against the **concurrent window**: every log entry with
  `seq > baseSeq`. This is the only transformation step in v1 (one-way, server-side).
* After sequencing, the origin receives an `ack` (exact entries); peers receive an `ops`
  broadcast with the same entries. All clients apply identical ops in identical order ⇒
  **convergence by construction**.

## 4. Transform rules

`transform(op, prior)` returns `op'` — the form of `op` that has the same effect when applied
*after* `prior` — or `null` when `op` is a satisfied no-op.

### 4.1 Insert vs insert (same block)

```
if prior.offset <= op.offset:  op'.offset = op.offset + len(prior.text)
else:                          op'.offset = op.offset
```

Equal offsets: the later-sequenced insert goes after (offset grows by the prior's length). This
is the tie-break that makes concurrent same-position inserts deterministic.

### 4.2 Insert vs delete (same block)

Let `prior` delete `[dStart, dEnd)`, and `op` insert at `tStart`:

```
tStart >= dEnd : op'.offset = tStart - (dEnd - dStart)   // deleted region before insert
tStart >= dStart: op'.offset = dStart                     // insert lands at deletion start
else            : op'.offset = tStart                     // before the deleted region
```

### 4.3 Range op vs delete (deleteText / applyFormatting / deleteHyperlink / applyTrackChange)

Let the range be `[tStart, tEnd)` and the prior delete `[dStart, dEnd)`:

```
dEnd <= tStart                : offset -= (dEnd - dStart)               // delete before range
dStart >= tEnd                : unchanged                               // delete after range
dStart <= tStart && dEnd>=tEnd: null                                    // range fully deleted
otherwise                     : offset' = tStart - max(0, min(dEnd,tStart)-dStart)
                                length' = len - overlapLen               // shrink by overlap
```

The surviving portion of a partially deleted range is represented as a single contiguous span
after the deletion (v1 simplification — acceptable because the server re-renders full HTML, so
split-span precision is not required for correctness).

### 4.4 Block ops

**Insert block** (insertParagraph / insertTable) at `pb` vs prior ops:

* prior insert at `pb' ≤ op.block` ⇒ `op'.block = op.block + 1` (blocks ≥ insertion point shift up)
* otherwise unchanged

**Delete block** (deleteParagraph / deleteTable) at `pb`:

* op inserts at `b`:
  * `b ≥ pb + 1` ⇒ `op'.block = b - 1` (insert "before the block now at b")
  * otherwise unchanged
* op is a text op at `b == pb` ⇒ `null` (the target block is gone)
* op is a text op at `b > pb` ⇒ `op'.block = b - 1`
* op is a block delete at `b == pb` ⇒ `null` (already deleted)

**Table rows** (insertTableRow / deleteTableRow) at `row`:

* prior insert at `pRow`: `op.row ≥ pRow` ⇒ `op'.row = row + 1`
* prior delete at `pRow`: `op.row == pRow` ⇒ `null`; `op.row > pRow` ⇒ `row - 1`

### 4.5 ID-based ops

* `deleteImage` twice with the same `imageId` ⇒ second is `null`.
* `resolveComment` twice ⇒ second is `null`.
* `acceptTrackChange`/`rejectTrackChange` on a `changeId` already decided (either way) ⇒ `null`.
  First sequenced wins; the loser's intent is satisfied.

### 4.6 Composition

`transformAgainst(op, priors)` applies the rules in order and stops at the first `null`
(no-op).

## 5. No-op semantics

A no-op is still **sequenced and acknowledged** — the client's optimistic UI is simply
superseded by the server's authoritative re-render. No-ops exist so that concurrent edits do not
turn into spurious rejections (the 10-users-same-paragraph criterion).

## 6. Rejection semantics

Genuinely invalid ops (out-of-range block/offset/length, missing payload, deleting the last
table row, empty data URI) **reject the whole batch**. The client rolls back its optimistic UI
and requests a resync. This is the atomicity contract: *either every op in the batch sequences,
or none do*.

## 7. Undo / redo

* The server keeps the full operation log per session.
* `undo` resolves server-side: the requester's ops down to `uptoSeq` are inverted
  (`Inverse.For`) and the inverses are sequenced as a new batch, so all clients converge.
* Inverse table (v1): insertText↔deleteText, applyFormatting→inverted format,
  insertParagraph↔deleteParagraph, insertTable↔deleteTable, insertImage→deleteImage,
  insertHyperlink→deleteHyperlink, resolveComment→resolveComment.
* No safe inverse exists for deleteImage / addComment / track-change decisions in v1 — undo of
  those is rejected with "nothing to undo" (the op remains in the log).

## 8. Why OT and not CRDT

1. **OOXML is opaque.** A CRDT replicates a fine-grained state model; DOCX would still need a
   full reconciliation pass, making the CRDT a parallel truth that can drift from the package.
2. **Validation.** The server validates each batch against the live OOXML. A CRDT has no
   natural place for "this op is malformed" — it just merges.
3. **Small op set.** The 15 v1 ops have simple, testable transforms (≈60 tests in
   TransformTests.cs). CRDT's payoff is unbounded op sets and offline editing, neither of which
   v1 needs.
4. **Determinism by construction.** Server sequencing + same-order application means convergence
   is guaranteed without commutative/associative merge proofs.

## 9. Convergence test plan

| Scenario | Expectation |
|---|---|
| 10 users type in the same paragraph, same offset | All inserts land in seq order; final text identical everywhere |
| 2 users delete overlapping ranges | Survivors' ranges shrink; no rejects |
| 1 user deletes a paragraph while another types into it | Typing becomes a no-op; no rejects |
| 2 users delete the same paragraph | Second delete is a no-op; document stays valid |
| 2 users accept/reject the same change | First decision wins; second is a no-op |
| 2 users insert paragraphs at the same index | Both land, ordered by seq; block indices shifted |
