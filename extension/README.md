# Marksmith Connector (Chrome / Edge extension)

Sends AI chat replies from **ChatGPT, Gemini, and Claude** web UIs straight into the
Marksmith desktop app via its local REST API — converted to Markdown, classified, and
normalized on arrival.

## Install (30 seconds, one time)

1. Open `chrome://extensions` (or `edge://extensions`) in your browser.
2. Turn on **Developer mode** (toggle, top-right).
3. Click **Load unpacked** and select this `extension` folder.
4. In Marksmith: **1 · Source → Automation → Local REST API → On** (already on if the
   badge URL shows). Ports must match — both default to `47821`.

## Use

| Action | What it does |
| --- | --- |
| Click the toolbar **M** button on a chat page | Sends the **latest assistant reply** |
| Right-click page → *Send full conversation to Marksmith* | Sends **every assistant reply**, separated by rules |
| Select any text on any site → right-click → *Send selection to Marksmith* | Sends the selection as-is |

A Windows notification confirms each send; the app preview updates instantly with the
detected source badge (e.g. "ChatGPT · 100% · 8 fixes").

## Options — auto-send & output profile

Open the extension's **Options** page (right-click the toolbar icon → *Options*, or via
`chrome://extensions`). Two things live there:

**Auto-send at the end of a conversation.** Turn on **Auto-send when I stop interacting**
and set an idle delay (seconds). When you stop typing/clicking in a chat for that long, the
whole conversation is sent to Marksmith automatically — no button press. Pair it with the app's
**Automation → Auto-generate from AI-chat ingests** to get a finished document every time.

**Output profile.** The connector can drive the app's output settings for anything it sends,
independently of the app's own Style panel — so automated exports use *these* settings:

| Setting | What it controls |
| --- | --- |
| **Output format** | PDF, DOCX, PDF + DOCX, PowerPoint, or EPUB for each send (non-PDF needs Marksmith Pro) |
| **Diagrams in Word** | **ShapeForge™** (rebuild as editable Word shapes) or **Snapshot** (embed a picture) |
| **Theme** | Any of the app's themes (the list auto-populates from the running app) |
| **Page width / A4 lock / single continuous page** | Layout of the exported document |
| **Normalize AI quirks / no-emoji / em-dash handling** | Cleanup applied on the way in |
| **Heading shift / bold / italic** | Formatting personalization (Pro) |
| **Output folder** | Where automated exports are written |

Click **Save** — the profile is stored in `chrome.storage.sync` and sent with every capture.
Leave the profile untouched to just use whatever the app is set to.

## Notes

- Talks only to `http://127.0.0.1:<port>` — nothing leaves your machine.
- Change the port via the extension's **Options** page if you changed it in the app.
- Chat sites redesign their DOM occasionally; if "latest reply" extraction misses,
  the selection method always works.
