# Marksmith Connector (Chrome / Edge extension)

Sends AI chat replies from **ChatGPT, Gemini, and Claude** web UIs straight into the
Marksmith desktop app via its local REST API — converted to Markdown, classified, and
normalized on arrival.

## Install (30 seconds, one time)

1. Open `chrome://extensions` (or `edge://extensions`) in your browser.
2. Turn on **Developer mode** (toggle, top-right).
3. Click **Load unpacked** and select this folder (`C:\temp\md_to_pdf_tui\extension`).
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

## Notes

- Talks only to `http://127.0.0.1:<port>` — nothing leaves your machine.
- Change the port via the extension's **Options** page if you changed it in the app.
- Chat sites redesign their DOM occasionally; if "latest reply" extraction misses,
  the selection method always works.
