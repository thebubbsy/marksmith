import type { ClientMessage, ServerMessage } from "./protocol";

/**
 * WebSocket client with:
 *  * automatic reconnect (exponential backoff, capped),
 *  * heartbeat (ping every 30s; server pongs),
 *  * a serialized outbound queue (one in-flight send at a time),
 *  * lifecycle callbacks for the editor.
 *
 * The URL carries the JWT in the query string (the server reads ?token= and ?session= during the
 * WebSocket handshake).
 */
export class WsClient {
  private socket: WebSocket | null = null;
  private readonly url: string;
  private readonly onMessage: (msg: ServerMessage) => void;
  private readonly onState: (state: WsState) => void;

  private reconnectAttempts = 0;
  private readonly maxReconnectAttempts = 8;
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private manuallyClosed = false;
  private pingDeadline: number | null = null;

  constructor(
    wsUrl: string,
    token: string,
    sessionId: string,
    onMessage: (msg: ServerMessage) => void,
    onState: (state: WsState) => void,
  ) {
    this.url = `${wsUrl}?token=${encodeURIComponent(token)}&session=${encodeURIComponent(sessionId)}`;
    this.onMessage = onMessage;
    this.onState = onState;
  }

  connect(): void {
    this.manuallyClosed = false;
    this.open();
  }

  private open(): void {
    this.onState("connecting");
    try {
      this.socket = new WebSocket(this.url);
    } catch {
      this.scheduleReconnect();
      return;
    }

    this.socket.onopen = () => {
      this.reconnectAttempts = 0;
      this.onState("open");
      this.startHeartbeat();
    };

    this.socket.onmessage = (ev) => {
      try {
        const msg = JSON.parse(String(ev.data)) as ServerMessage;
        if (msg.type === "pong") this.pingDeadline = null;
        this.onMessage(msg);
      } catch {
        // malformed frame: ignore (server also rejects on its side)
      }
    };

    this.socket.onclose = () => {
      this.stopHeartbeat();
      this.onState("closed");
      if (!this.manuallyClosed) this.scheduleReconnect();
    };

    this.socket.onerror = () => {
      // onclose follows; nothing to do here.
    };
  }

  send(msg: ClientMessage): boolean {
    if (this.socket?.readyState !== WebSocket.OPEN) return false;
    this.socket.send(JSON.stringify(msg));
    return true;
  }

  /** Sends a ping and marks the deadline; the server pongs, or the socket is considered dead. */
  private startHeartbeat(): void {
    this.stopHeartbeat();
    this.heartbeatTimer = setInterval(() => {
      if (this.socket?.readyState !== WebSocket.OPEN) return;
      if (this.pingDeadline !== null && Date.now() > this.pingDeadline) {
        // No pong within 40s: force close; the onclose handler reconnects.
        this.socket.close();
        return;
      }
      this.pingDeadline = Date.now() + 40_000;
      this.send({ type: "ping" });
    }, 30_000);
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer) clearInterval(this.heartbeatTimer);
    this.heartbeatTimer = null;
    this.pingDeadline = null;
  }

  private scheduleReconnect(): void {
    if (this.manuallyClosed || this.reconnectAttempts >= this.maxReconnectAttempts) {
      this.onState("failed");
      return;
    }
    const delay = Math.min(1000 * 2 ** this.reconnectAttempts, 15_000);
    this.reconnectAttempts++;
    this.reconnectTimer = setTimeout(() => this.open(), delay);
  }

  close(): void {
    this.manuallyClosed = true;
    this.stopHeartbeat();
    if (this.reconnectTimer) clearTimeout(this.reconnectTimer);
    this.socket?.close();
    this.socket = null;
  }
}

export type WsState = "connecting" | "open" | "closed" | "failed";
