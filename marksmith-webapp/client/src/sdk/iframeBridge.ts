/**
 * iframe host bridge: runs inside the sandboxed iframe, reads its config from the URL hash,
 * mounts the editor, and proxies host commands (resync / undo) and editor events back out.
 *
 * The postMessage surface is deliberately tiny (docs/06-sdk-api.md):
 *   host -> iframe : { source:"marksmith-sdk", type:"resync" | "undo" }
 *   iframe -> host : { source:"marksmith-editor", type:"ready" | "error" | "state", ... }
 */
import { init } from "./index";

interface IframeConfig {
  documentId: string;
  userId: string;
  token?: string;
  wsUrl: string;
  apiUrl: string;
  suggestionMode?: boolean;
}

function readConfig(): IframeConfig | null {
  const hash = window.location.hash.replace(/^#/, "");
  if (!hash) return null;
  try {
    return JSON.parse(decodeURIComponent(hash)) as IframeConfig;
  } catch {
    return null;
  }
}

export function bootIframe(): void {
  const config = readConfig();
  const host = document.getElementById("marksmith-root");

  if (!config || !host) {
    post("error", { message: "invalid iframe config" });
    return;
  }

  host.innerHTML = "";
  void init(host, {
    wsUrl: config.wsUrl,
    apiUrl: config.apiUrl,
    documentId: config.documentId,
    userId: config.userId,
    token: config.token,
    suggestionMode: config.suggestionMode,
  }).then(() => post("ready", { documentId: config.documentId }));

  window.addEventListener("message", (ev) => {
    const msg = ev.data as { source?: string; type?: string };
    if (msg.source !== "marksmith-sdk") return;
    // Forward host commands to the editor via custom events the container listens for.
    host.dispatchEvent(new CustomEvent("marksmith:command", { detail: { type: msg.type } }));
  });
}

function post(type: string, detail: Record<string, unknown>): void {
  window.parent.postMessage({ source: "marksmith-editor", type, ...detail }, "*");
}
