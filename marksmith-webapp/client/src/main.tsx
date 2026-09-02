import { createRoot } from "react-dom/client";
import { MarksmithEditor } from "./sdk/MarksmithEditor";
import "./styles/theme.css";

interface HashConfig {
  documentId?: string;
  userId?: string;
  wsUrl?: string;
  apiUrl?: string;
}

function readHashConfig(): HashConfig | null {
  const hash = window.location.hash.replace(/^#/, "");
  if (!hash) return null;
  try {
    return JSON.parse(decodeURIComponent(hash)) as HashConfig;
  } catch {
    return null;
  }
}

const hashCfg = readHashConfig();
const params = new URLSearchParams(window.location.search);

const isViteDev = window.location.port === "5173";
const defaultBackendHost = isViteDev ? "localhost:5210" : window.location.host;
const defaultApiOrigin = isViteDev ? "http://localhost:5210" : window.location.origin;

const wsUrl = hashCfg?.wsUrl ?? params.get("ws") ?? `ws://${defaultBackendHost}/ws`;
const apiUrl = hashCfg?.apiUrl ?? params.get("api") ?? defaultApiOrigin;
const documentId = hashCfg?.documentId ?? params.get("doc") ?? "marksmith-masterpiece";
const userId = hashCfg?.userId ?? params.get("user") ?? `author-${Math.floor(Math.random() * 899 + 100)}`;

const rootEl = document.getElementById("root")!;

// iframe host bridge: forward host commands (postMessage) to the editor's command channel.
window.addEventListener("message", (ev) => {
  const msg = ev.data as { source?: string; type?: string };
  if (msg.source !== "marksmith-sdk") return;
  rootEl.dispatchEvent(new CustomEvent("marksmith:command", { detail: { type: msg.type } }));
});

createRoot(rootEl).render(
  <MarksmithEditor
    wsUrl={wsUrl}
    apiUrl={apiUrl}
    documentId={documentId}
    userId={userId}
    onReady={(info) => console.info("[MarkSmith] Collaboration Ready:", info)}
    onError={(code, message) => console.error("[MarkSmith] Collaboration Error:", code, message)}
  />,
);
