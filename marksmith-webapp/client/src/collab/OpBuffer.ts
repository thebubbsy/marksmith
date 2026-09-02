import type { Operation } from "./protocol";

/**
 * Client-side edit aggregation: a flurry of keystrokes becomes a single batch every
 * <batchWindowMs> (default 200ms). This is what keeps the server's full re-render cadence
 * acceptable: the render runs per batch, not per keystroke.
 */
export class OpBuffer {
  private ops: Operation[] = [];
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly flush: (ops: Operation[]) => void,
    private readonly batchWindowMs = 200,
    private readonly maxBatchSize = 64,
  ) {}

  push(op: Operation): void {
    this.ops.push(op);
    if (this.ops.length >= this.maxBatchSize) {
      this.flushNow();
      return;
    }
    if (this.timer === null) {
      this.timer = setTimeout(() => this.flushNow(), this.batchWindowMs);
    }
  }

  /** Drains and sends. Returns the ops that were flushed. */
  flushNow(): Operation[] {
    if (this.timer !== null) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    if (this.ops.length === 0) return [];
    const batch = this.ops;
    this.ops = [];
    this.flush(batch);
    return batch;
  }

  /** Number of pending ops (for the status bar). */
  get pendingCount(): number {
    return this.ops.length;
  }

  dispose(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
    this.ops = [];
  }
}
