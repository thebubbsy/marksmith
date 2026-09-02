/**
 * Wire protocol types — the TypeScript mirror of the C# contract (server/Ot/Operation.cs and
 * server/Web/WsProtocol.cs). Keep both sides in lockstep; the OT spec (docs/02-ot-spec.md)
 * documents every field.
 */

export type OpType =
  | "insertText"
  | "deleteText"
  | "applyFormatting"
  | "insertParagraph"
  | "deleteParagraph"
  | "insertTable"
  | "deleteTable"
  | "insertTableRow"
  | "deleteTableRow"
  | "insertImage"
  | "deleteImage"
  | "insertHyperlink"
  | "deleteHyperlink"
  | "addComment"
  | "resolveComment"
  | "applyTrackChange"
  | "acceptTrackChange"
  | "rejectTrackChange";

export interface Formatting {
  bold?: boolean;
  italic?: boolean;
  underline?: boolean;
  strikethrough?: boolean;
  color?: string;
}

/** A single operation; serialized flat (payload fields inline). */
export interface Operation {
  id: string;
  clientId: string;
  type: OpType;

  // position
  block?: number;
  offset?: number;
  length?: number;

  // content
  text?: string;
  style?: string;
  rows?: number;
  cols?: number;
  row?: number;

  // formatting
  format?: Formatting;

  // image
  alt?: string;
  dataUri?: string;
  url?: string;
  width?: number;
  height?: number;
  imageId?: string;

  // hyperlink
  href?: string;

  // comment / track change
  commentId?: string;
  author?: string;
  changeId?: string;
  kind?: "insert" | "delete" | "format";
}

// ---------------- server -> client ----------------

export interface WelcomeMessage {
  type: "welcome";
  sessionId: string;
  clientId: string;
  seq: number;
  html: string;
  docUrl?: string;
}

export interface AckEntry {
  seq: number;
  op: Operation;
  noOp: boolean;
}

export interface AckMessage {
  type: "ack";
  batchId: string;
  baseSeq: number;
  entries: AckEntry[];
}

export interface OpsEntry {
  seq: number;
  clientId: string;
  op: Operation;
}

export interface OpsMessage {
  type: "ops";
  entries: OpsEntry[];
  html?: string;
}

export interface ErrorMessage {
  type: "error";
  code: string;
  message: string;
}

export interface PongMessage {
  type: "pong";
}

export interface KickedMessage {
  type: "kicked";
  reason: string;
}

// ---------------- client -> server ----------------

export interface BatchMessage {
  type: "batch";
  batchId: string;
  baseSeq: number;
  ops: Operation[];
}

export interface UndoMessage {
  type: "undo";
  uptoSeq: number;
}

export interface CaretPosition {
  block: number;
  offset: number;
}

export interface SelectionSpan {
  start: CaretPosition;
  end: CaretPosition;
}

export interface PresenceMessage {
  type: "presence";
  caret?: CaretPosition;
  selection?: SelectionSpan;
}

export interface PresenceBroadcast {
  type: "presence";
  clientId: string;
  caret?: CaretPosition;
  selection?: SelectionSpan;
}

export interface ResyncMessage {
  type: "resync";
}

export interface PingMessage {
  type: "ping";
}

export type ServerMessage = WelcomeMessage | AckMessage | OpsMessage | ErrorMessage | PongMessage | KickedMessage | PresenceBroadcast;
export type ClientMessage = BatchMessage | UndoMessage | PresenceMessage | ResyncMessage | PingMessage;
