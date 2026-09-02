# Embeddable SDK API — Marksmith.WebApp v1

> Deliverable 06 of 12. Implementation: `client/src/sdk/` (React + vanilla + iframe).

## 1. Quick start

### React

```tsx
import { MarksmithEditor } from "@marksmith/webapp-client/sdk";

<MarksmithEditor
  wsUrl="wss://collab.example.com/ws"
  apiUrl="https://collab.example.com"
  documentId="doc-abc"
  userId="u-42"
  token={jwt}                 // optional: SDK can fetch a dev token
  suggestionMode={false}
  onReady={({ sessionId, seq }) => console.log("ready", sessionId, seq)}
  onError={(code, message) => console.error(code, message)}
/>
```

### Vanilla JS

```js
import { init } from "@marksmith/webapp-client/sdk";

const handle = await init(document.getElementById("editor"), {
  wsUrl: "wss://collab.example.com/ws",
  apiUrl: "https://collab.example.com",
  documentId: "doc-abc",
  userId: "u-42",
  token: jwt,          // optional
  height: "600px",
});

handle.undo();
handle.resync();
const blob = await handle.saveAsDocx();
```

## 2. Options

| Option | Type | Default | Notes |
|---|---|---|---|
| `wsUrl` | string | — | WebSocket base URL (`/ws` is appended) |
| `apiUrl` | string | — | REST base URL |
| `documentId` | string | — | the document; also the session id |
| `userId` | string | — | display name + identity |
| `token` | string? | — | JWT; when omitted the SDK calls `POST /api/auth/token` (dev only) |
| `suggestionMode` | boolean | false | all edits become tracked suggestions |
| `showToolbar` | boolean | true | render the toolbar |
| `showPanels` | boolean | true | render comments + track changes panels |
| `height` | string | "600px" | container height (vanilla init) |

## 3. Handle methods

| Method | Description |
|---|---|
| `destroy()` | unmount, close socket, clean up timers |
| `resync()` | request a fresh authoritative state from the server |
| `undo()` | server-side undo (last ops by this client) |
| `saveAsDocx(): Promise<Blob>` | download the current DOCX via `GET /api/sessions/{id}/docx` |

## 4. Modes

### 4.1 Standalone (recommended)

Full editing experience: clipboard, drag-drop, native selection, file picking for images.
`init()` / `<MarksmithEditor />` mount the editor in the host document — the host page and the
editor share the same origin/trust boundary.

### 4.2 iframe (sandboxed)

`mountIframe(container, { src, ...options })` mounts the sample UI in an iframe. The iframe
surface is intentionally limited:

| Limitation | v1 behavior | Workaround |
|---|---|---|
| Clipboard | `document.execCommand`-style paste may be blocked by permissions policy | Host captures paste via its own handlers and sends ops through the bridge (Phase 2) |
| Drag & drop files | Blocked inside sandbox | File picker button (toolbar) works — it is not drag-drop |
| Native selection details | Cross-iframe selection is not shared | Selection is confined to the iframe document |
| Focus | Clicking into the iframe moves focus; host shortcuts need explicit routing | Host listens for `ready` and forwards commands |

PostMessage surface:

```
host → iframe : { source:"marksmith-sdk", type:"resync" | "undo" }
iframe → host : { source:"marksmith-editor", type:"ready" | "error" | "state", ... }
```

The iframe config (documentId, userId, token, wsUrl, apiUrl) is passed via the URL hash
(`#` + JSON), decoded by `iframeBridge.ts`.

## 5. Themeing

Everything is driven by CSS variables (`client/src/styles/theme.css`):

```css
.host .ms-editor {
  --ms-bg: #ffffff;
  --ms-surface: #f7f7f8;
  --ms-text: #1b1b1f;
  --ms-accent: #0b63ce;
  --ms-border: #dcdce1;
  --ms-font: "Segoe UI", system-ui, sans-serif;
}
```

Dark mode follows `prefers-color-scheme` by default; hosts can override per-class. The editor
root is `.ms-editor`; sub-components use `.ms-toolbar`, `.ms-editor-surface`, `.ms-panels`,
`.ms-statusbar`.

## 6. Events

| Event | Payload | When |
|---|---|---|
| `onReady` | `{ sessionId, seq, clientId }` | after the first `welcome` |
| `onError` | `(code, message)` | boot failure, batch rejection, kicked |
| `marksmith:command` (vanilla) | `{ type: "resync" | "undo" }` | host-initiated commands on the container |
| presence | rendered in the PresenceRail | remote caret/selection |

## 7. Security notes for hosts

* Pass a **short-lived JWT scoped to one document**; never a long-lived master token.
* `token` in the URL hash (iframe mode) is visible in the browser's history — acceptable for
  dev; Phase 2 adds a cookie/subprotocol handshake.
* The SDK never reads the host's clipboard or DOM outside the editor root.
