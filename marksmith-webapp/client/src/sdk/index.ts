/**
 * Marksmith.WebApp SDK entry.
 *
 * Standalone mode: `MarksmithEditor.init(container, options)` — full editing experience.
 * iframe mode: `MarksmithEditor.mountIframe(container, options)` — sandboxed, with the
 * documented limitations (clipboard, drag-drop, selection), driven through a postMessage bridge.
 *
 * React consumers can import <MarksmithEditor /> directly (src/sdk/MarksmithEditor.tsx).
 */

import type { MarksmithEditorProps } from "./MarksmithEditor";

export type { MarksmithEditorProps } from "./MarksmithEditor";
export { MarksmithEditor } from "./MarksmithEditor";

export interface EditorOptions {
  wsUrl: string;
  apiUrl: string;
  documentId: string;
  userId: string;
  token?: string;
  suggestionMode?: boolean;
  showToolbar?: boolean;
  showPanels?: boolean;
  /** Height of the editor container (CSS). Default "600px". */
  height?: string;
}

export interface MarksmithEditorHandle {
  destroy(): void;
  resync(): void;
  undo(): void;
  /** Returns the current document as DOCX (downloads via the REST endpoint). */
  saveAsDocx(): Promise<Blob>;
}

/**
 * Vanilla JS entry: mounts the editor into an existing element.
 * Returns a handle with destroy/resync/undo/saveAsDocx.
 */
export async function init(container: HTMLElement, options: EditorOptions): Promise<MarksmithEditorHandle> {
  const { createRoot } = await import("react-dom/client");
  const React = await import("react");
  const { MarksmithEditor } = await import("./MarksmithEditor");

  container.classList.add("ms-sdk-container");
  container.style.height = options.height ?? "600px";

  const root = createRoot(container);
  const props: MarksmithEditorProps = {
    wsUrl: options.wsUrl,
    apiUrl: options.apiUrl,
    documentId: options.documentId,
    userId: options.userId,
    token: options.token,
    suggestionMode: options.suggestionMode,
    showToolbar: options.showToolbar ?? true,
    showPanels: options.showPanels ?? true,
  };
  root.render(React.createElement(MarksmithEditor, props));

  return {
    destroy() {
      root.unmount();
      container.classList.remove("ms-sdk-container");
    },
    resync() {
      dispatch(container, { type: "resync" });
    },
    undo() {
      dispatch(container, { type: "undo" });
    },
    async saveAsDocx() {
      const res = await fetch(`${options.apiUrl}/api/sessions/${options.documentId}/docx`, {
        headers: options.token ? { Authorization: `Bearer ${options.token}` } : undefined,
      });
      if (!res.ok) throw new Error(`save failed: ${res.status}`);
      return res.blob();
    },
  };
}

function dispatch(container: HTMLElement, detail: Record<string, unknown>): void {
  container.dispatchEvent(new CustomEvent("marksmith:command", { detail }));
}

/**
 * iframe mode: mounts the editor inside an <iframe> pointing at the sample UI, and bridges
 * commands through postMessage. The iframe surface intentionally exposes only the v1 operation
 * set. Documented limitations: clipboard paste/formatting, drag-drop of files, and some native
 * selection behaviors are restricted inside the sandbox — see docs/06-sdk-api.md.
 */
export function mountIframe(container: HTMLElement, options: EditorOptions & { src: string }): MarksmithEditorHandle {
  const iframe = document.createElement("iframe");
  iframe.src = `${options.src}#${encodeURIComponent(JSON.stringify({
    documentId: options.documentId,
    userId: options.userId,
    token: options.token,
    wsUrl: options.wsUrl,
    apiUrl: options.apiUrl,
  }))}`;
  iframe.className = "ms-iframe";
  iframe.style.width = "100%";
  iframe.style.height = options.height ?? "600px";
  iframe.style.border = "0";
  container.appendChild(iframe);

  const handle = {
    destroy() {
      iframe.remove();
    },
    resync() {
      iframe.contentWindow?.postMessage({ source: "marksmith-sdk", type: "resync" }, "*");
    },
    undo() {
      iframe.contentWindow?.postMessage({ source: "marksmith-sdk", type: "undo" }, "*");
    },
    async saveAsDocx(): Promise<Blob> {
      const res = await fetch(`${options.apiUrl}/api/sessions/${options.documentId}/docx`, {
        headers: options.token ? { Authorization: `Bearer ${options.token}` } : undefined,
      });
      if (!res.ok) throw new Error(`save failed: ${res.status}`);
      return res.blob();
    },
  };

  // Listen for iframe -> parent requests (e.g. clipboard reads that need parent cooperation).
  window.addEventListener("message", (ev) => {
    const msg = ev.data as { source?: string; type?: string };
    if (msg.source !== "marksmith-editor") return;
    // v1: echo readiness so the host knows the editor loaded.
    if (msg.type === "ready") {
      iframe.contentWindow?.postMessage({ source: "marksmith-sdk", type: "ready-ack" }, "*");
    }
  });

  return handle;
}
