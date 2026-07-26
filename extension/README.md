# Marksmith Connector (Chrome / Edge extension)

Turns AI chat replies from **ChatGPT, Gemini, Claude, and Copilot** web UIs into polished
documents — by talking to the Marksmith desktop app's local REST API. Two ways to finish:

- **Send to Marksmith** — push the reply into the running app's preview (`/api/ingest`),
  classified, normalized, and badged with its source on arrival.
- **Download directly** — get finished **PDF / DOCX / PPTX / EPUB** bytes back from the app
  (`/api/convert`) and save them straight to your browser's Downloads folder. *No need to
  open Marksmith at all.*

It also adds a **"Copy as Markdown"** button beneath every assistant reply on those sites —
styled to match each site so it feels native. One click copies the reply with headings,
tables, and code fences intact. If the Marksmith app spots a plain-text paste, it asks the
extension to **pulse the button** in your browser so you know it's there.

## Install (30 seconds, one time)

1. Open `chrome://extensions` (or `edge://extensions`) in your browser.
2. Turn on **Developer mode** (toggle, top-right).
3. Click **Load unpacked** and select this `extension` folder.
4. In Marksmith: **1 · Source → Automation → Local REST API → On** (already on if the
   badge URL shows). Ports must match — both default to `47821`.
5. *(If sends are blocked)* copy the extension's ID — shown in the popup when offline, or on
   `chrome://extensions` — into the app's **Automation → Allowed extension ID**.

## The popup control center

Click the toolbar **M** button on any chat page. The popup shows, at a glance:

| Section | What it tells you |
| --- | --- |
| **Connection badge** | Green *Connected* / red *Offline* — a live `GET /api/health` check |
| **Detected source** | The site (ChatGPT/Gemini/…), the model, and the reply's char count |
| **Classification** | The app's AI-source confidence (`POST /api/classify`) + a *Σ math* chip when formulas are present |
| **Scope toggle** | *Latest reply* vs *Full conversation* |

…and gives you four actions:

- **Send to Marksmith** — ingest into the app preview (same as before, now with confirmation).
- **Download PDF / DOCX / PPTX / EPUB** — convert via the app and save the file in-browser.
  Uses your saved **output profile** (theme, width, diagram mode…) with the format forced to
  whichever button you pressed.
- **Copy as Markdown** — the raw Markdown of the current scope, to your clipboard.

If the app can't be reached, the popup shows a short checklist and your **extension ID**
(click to copy) so you can pair it in one step.

The toolbar icon also wears a small red **!** whenever the app is unreachable (polled once a
minute), so you know before you click.

## Right-click menu

| Action | What it does |
| --- | --- |
| *Send full conversation to Marksmith* | Sends **every assistant reply**, separated by rules |
| *Download latest reply as PDF* | Converts the newest reply and saves a `.pdf` |
| *Download latest reply as DOCX* | Converts the newest reply and saves a `.docx` |
| *Send selection to Marksmith* (any site) | Sends the selected text as-is |

A Windows notification confirms each send/download.

## Options — auto-send & output profile

Open the extension's **Options** page (right-click the toolbar icon → *Options*, or via the
popup's *⚙ Options* link). Two things live there:

**Auto-send at the end of a conversation.** Turn on **Auto-send when I stop interacting**
and set an idle delay (seconds). When you stop typing/clicking in a chat for that long, the
whole conversation is sent to Marksmith automatically — no button press. Pair it with the app's
**Automation → Auto-generate from AI-chat ingests** to get a finished document every time.

**Output profile.** The connector drives the app's output settings for anything it sends *or
downloads*, independently of the app's own Style panel:

| Setting | What it controls |
| --- | --- |
| **Output format** | Default format for auto-sends (PDF, DOCX, PDF + DOCX, PowerPoint, EPUB — non-PDF needs Marksmith Pro). Popup downloads override this per-click. |
| **Diagrams in Word** | **ShapeForge™** (rebuild as editable Word shapes) or **Snapshot** (embed a picture) |
| **Theme** | Any of the app's themes (the list auto-populates from the running app) |
| **Page width / A4 lock / single continuous page** | Layout of the exported document |
| **Normalize AI quirks / no-emoji / em-dash handling** | Cleanup applied on the way in |
| **Heading shift / bold / italic** | Formatting personalization (Pro) |
| **Output folder** | Where the app's own automated exports are written (browser downloads go to your browser's Downloads folder) |

Click **Save** — the profile is stored in `chrome.storage.sync` and sent with every capture.
Leave the profile untouched to just use whatever the app is set to.

## Notes

- Talks only to `http://127.0.0.1:<port>` — nothing leaves your machine.
- Change the port via the extension's **Options** page if you changed it in the app.
- Chat sites redesign their DOM occasionally; if "latest reply" extraction misses,
  the selection method always works.
- Downloads hand the file to your browser's download manager, so they respect your browser's
  download settings (folder, "ask where to save", etc.).
