import { WsClient, type WsState } from "./WsClient";
import { OpBuffer } from "./OpBuffer";
import { transformAgainst } from "./transform";
import type {
  AckMessage, AckEntry, BatchMessage, CaretPosition, Operation, OpsMessage,
  PresenceMessage, SelectionSpan, ServerMessage,
} from "./protocol";

export interface CollabHandlers {
  /** Server pushed a full fresh render (welcome / resync). */
  onInitialState(html: string, seq: number): void;
  /** Remote ops (from other users) that must be applied locally. */
  onRemoteOps(entries: OpsMessage["entries"]): void;
  /** Our own batch was acked: entries are the sequenced (post-transform) ops. */
  onAck(entries: AckEntry[], baseSeq: number): void;
  /** Batch rejected: caller should roll back the optimistic UI. */
  onRejected(code: string, message: string): void;
  onStateChange(state: WsState): void;
  /** Remote user presence (caret/selection). */
  onPresence(clientId: string, caret?: CaretPosition, selection?: SelectionSpan): void;
}

/**
 * The client side of the OT pipeline:
 *
 *   1. Local edits are optimistically applied to the DOM immediately.
 *   2. They are also queued in the 200ms OpBuffer as pending operations.
 *   3. On flush, a BatchMessage{baseSeq, ops} goes to the server.
 *   4. If remote ops arrive while we have pending ops, the pending ops are rebased against them
 *      (one-way transform, mirrored in TS) so the local DOM stays consistent with the server's
 *      sequencing.
 *   5. On ack, the pending ops are dropped (they are now in the server log) and baseSeq advances.
 */
export class CollabClient {
  private ws: WsClient;
  private buffer: OpBuffer;
  private readonly handlers: CollabHandlers;

  private baseSeq = 0;
  private lastOwnBatchFirstSeq = 0;
  private pending: Operation[] = [];
  private batchCounter = 0;
  private clientId = "anonymous";
  private sessionId = "";
  private lastRemoteHtmlSeq = 0;

  constructor(
    wsUrl: string,
    token: string,
    sessionId: string,
    handlers: CollabHandlers,
    batchWindowMs = 200,
  ) {
    this.handlers = handlers;
    this.sessionId = sessionId;
    this.buffer = new OpBuffer((ops) => this.sendBatch(ops), batchWindowMs);
    this.ws = new WsClient(
      wsUrl,
      token,
      sessionId,
      (msg) => this.onServerMessage(msg),
      (state) => {
        this.wsState = state;
        this.handlers.onStateChange(state);
      },
    );
  }

  connect(): void {
    this.ws.connect();
  }

  disconnect(): void {
    this.ws.close();
    this.buffer.dispose();
  }

  get state(): WsState {
    return this.wsState;
  }
  private wsState: WsState = "closed";

  /** The server-assigned client id (valid after the welcome frame). */
  getClientId(): string {
    return this.clientId;
  }

  /** The active session id. */
  getSessionId(): string {
    return this.sessionId;
  }

  /** Registers the DOM applier used for remote ops (set by the editor surface). Pass null to unregister. */
  setRemoteOpsApplier(fn: ((entries: OpsMessage["entries"]) => void) | null): void {
    this.remoteOpsApplier = fn;
  }
  private remoteOpsApplier: ((entries: OpsMessage["entries"]) => void) | null = null;

  /** Submits a local op: optimistic DOM apply is the caller's job; this queues + batches it. */
  submit(op: Operation): void {
    op.clientId = this.clientId;
    this.pending.push(op);
    this.buffer.push(op);
  }

  /** Server-side undo: the client just asks; the server resolves inverses from its log. */
  undo(uptoSeq?: number): void {
    this.flushPending();
    this.ws.send({ type: "undo", uptoSeq: uptoSeq ?? this.lastOwnBatchFirstSeq });
  }

  /** Requests a full re-sync from the server (drift recovery). */
  resync(): void {
    this.ws.send({ type: "resync" });
  }

  sendPresence(caret?: CaretPosition, selection?: SelectionSpan): void {
    const msg: PresenceMessage = { type: "presence" };
    if (caret) msg.caret = caret;
    if (selection) msg.selection = selection;
    this.ws.send(msg);
  }

  private flushPending(): void {
    this.buffer.flushNow();
  }

  private sendBatch(ops: Operation[]): void {
    if (ops.length === 0) return;
    const msg: BatchMessage = {
      type: "batch",
      batchId: `b-${this.clientId}-${++this.batchCounter}`,
      baseSeq: this.baseSeq,
      ops,
    };
    if (!this.ws.send(msg)) {
      // Socket not open: keep the ops pending; they will be resent on reconnect via resync.
      // (v1: on reconnect the server sends a fresh welcome, so the caller re-syncs and we drop
      // the pending queue rather than risk double-applying.)
    }
  }

  private onServerMessage(msg: ServerMessage): void {
    switch (msg.type) {
      case "welcome": {
        this.clientId = msg.clientId;
        this.baseSeq = msg.seq;
        this.pending = [];
        this.lastRemoteHtmlSeq = msg.seq;
        this.handlers.onInitialState(msg.html, msg.seq);
        break;
      }
      case "ops":
        this.onRemoteOps(msg);
        break;
      case "ack":
        this.onAck(msg);
        break;
      case "error":
        if (msg.code === "batch_rejected") {
          this.handlers.onRejected(msg.code, msg.message);
          // The optimistic UI is stale; ask the server for the authoritative state.
          this.ws.send({ type: "resync" });
        } else {
          this.handlers.onRejected(msg.code, msg.message);
        }
        break;
      case "presence":
        this.handlers.onPresence(msg.clientId, msg.caret, msg.selection);
        break;
      case "kicked":
        this.handlers.onRejected("kicked", msg.reason);
        break;
      case "pong":
        break; // heartbeat handled inside WsClient
    }
  }

  private onRemoteOps(msg: OpsMessage): void {
    // Rebase pending local ops against the remote ops (one-way transform), then tell the UI to
    // apply the remote ops. The rebased pending ops stay queued for the next flush.
    if (this.pending.length > 0) {
      this.pending = this.pending
        .map((op) => transformAgainst(op, msg.entries.map((e) => e.op)))
        .filter((op): op is Operation => op !== null);
    }
    const maxSeq = msg.entries.reduce((m, e) => Math.max(m, e.seq), this.lastRemoteHtmlSeq);
    this.lastRemoteHtmlSeq = maxSeq;
    this.handlers.onRemoteOps(msg.entries);
    this.remoteOpsApplier?.(msg.entries);
  }

  private onAck(msg: AckMessage): void {
    // Drop the acked ops from pending and advance baseSeq to the ack's highest seq.
    const ackedIds = new Set(msg.entries.map((e) => e.op.id));
    this.pending = this.pending.filter((op) => !ackedIds.has(op.id));
    const maxSeq = msg.entries.reduce((m, e) => Math.max(m, e.seq), msg.baseSeq);
    this.baseSeq = Math.max(this.baseSeq, maxSeq);
    // Track the first seq of our last sequenced batch for server-side undo ("undo my last batch").
    const mySeqs = msg.entries.filter((e) => e.op.clientId === this.clientId).map((e) => e.seq);
    if (mySeqs.length > 0) this.lastOwnBatchFirstSeq = Math.min(...mySeqs);
    this.handlers.onAck(msg.entries, msg.baseSeq);
  }
}
