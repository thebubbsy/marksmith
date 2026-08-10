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
| *Send selection to MarkSmith* (any site) | Converts the selected fragment to Markdown (headings, bold, lists, code fences…) — falls back to raw text when there's nothing to format |

A Windows notification confirms each send/download.

## Options — auto-send & output profile

Open the extension's **Options** page (right-click the toolbar icon → *Options*, or via the
popup's *⚙ Options* link). Two things live there:

**Auto-send at the end of a conversation.** Turn on **Auto-send when I stop interacting**
and set an idle delay (seconds). When you stop typing/clicking in a chat for that long, the
whole conversation is sent to Marksmith automatically — no button press. Pair it with the app's
**Automation → Auto-generate from AI-chat ingests** to get a finished document every time.

**Output profile — your app's settings, live.** The extension does *not* keep its own copy of
your output settings. Every field defaults to **App default**: whatever you've saved in the
Marksmith app's Style panel at that moment wins, and changes there take effect on the very next
capture — no re-saving here. Set a field in the extension only if you want extension captures
to *override* your in-app choice (e.g. AI chats always get no-emoji + normalized dashes while
your in-app work keeps its own style). The page shows how many fields you're currently
overriding, and **Reset to app defaults** clears them all in one click.

| Override | What it controls |
| --- | --- |
| **Theme** | Any of the app's themes (the list auto-populates from the running app) |
| **Output format** | Format for auto-sends (PDF, DOCX, PDF + DOCX, PowerPoint, EPUB). Popup downloads always use the button you pressed. |
| **Page width / A4 lock / single continuous page** | Layout of the exported document |
| **Diagrams in Word** | **ShapeForge™** (rebuild as editable Word shapes) or **Snapshot** (embed a picture) |
| **Oversized diagrams** | All 8 strategies from the app: Ask me each time, Keep Original Size, Gentle Shrink (max 75%), Slice Vertically, Aggressive Shrink, and the Compress family (Gaps / Nodes / Both) |
| **Render Mermaid diagrams** | Force diagram rendering on/off for every capture |
| **Smart connectors / line routing / line arrowheads** | ShapeForge connector styles — routing (straight / elbow / curved) and all 7 arrowheads including diamond & oval |
| **Font preset / PDF page numbers / file name template / author name** | Typography, export chrome, and DOCX creator metadata |
| **PDF header / footer templates** | Per-page chrome with `{title}` `{page}` `{pages}` `{date}` tokens |
| **PDF security** | Password protection (user + owner) and allow printing / copying / modifying for PDF exports |
| **Table of contents / word count / source attribution / cover page** | Document extras |
| **Normalize AI quirks / no-emoji / em-dash handling** | Cleanup applied on the way in |
| **Heading shift / bold / italic** | Formatting personalization |
| **Page border / Track Changes** | DOCX page chrome |
| **Output folder** | Where the app's automated exports are written (browser downloads go to your browser's Downloads folder) |

Click **Save** — only your explicit overrides are stored (`chrome.storage.sync`) and sent with
each capture; everything else follows the app. Source details (which AI site, the model, chat
title, language & accent color) are still captured automatically on every send.

## Notes

- Talks only to `http://127.0.0.1:<port>` — nothing leaves your machine.
- Change the port via the extension's **Options** page if you changed it in the app.
- Chat sites redesign their DOM occasionally; if "latest reply" extraction misses,
  the selection method always works.
- **Mermaid diagrams are captured as real diagrams.** On GitHub (and anywhere else that
  keeps the source in `<pre lang="mermaid">` or hides it behind a rendered SVG), the
  selection capture recovers the raw diagram source and ships it as a ` ```mermaid `
  fence — so Marksmith re-renders every chart instead of dropping it. Screen-reader-only
  chrome (GitHub's "Loading" spinners, "Copy code" labels) is stripped on the way out.
- **Images in a selection are captured automatically.** Highlight over an `<img>` and it
  ships with its **real, absolute URL** — lazy-loaded sources (`data-src`), responsive
  `srcset`/`<picture>` picks, and relative paths are all resolved, while tracking pixels and
  placeholder dots are skipped. By default images are sent as **links** (`![alt](url)` — small
  & fast; Marksmith downloads them for the PDF/DOCX). Prefer the pixels baked in? You can
  **embed them as base64** instead (permanent & offline-proof, but larger): when a selection
  contains images you're asked which way, with a *remember my choice* tick box — and a
  permanent default lives in **Options → Images in a selection**. Embedding uses an optional,
  one-time permission to fetch images past site restrictions (asked only when you use it —
  nothing extra at install, so it stays Chrome Web Store-friendly); anything it can't fetch
  falls back to a link.
- Downloads hand the file to your browser's download manager, so they respect your browser's
  download settings (folder, "ask where to save", etc.).
