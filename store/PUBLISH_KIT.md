# Chrome Web Store — Publish Kit for Marksmith Connector

> **IMPORTANT — two versions now exist.** v1.0.0 is the simple connector (toolbar/right-click send).
> v1.1.0 adds AI-usage governance, which injects content scripts on chatgpt.com / gemini.google.com /
> claude.ai. Always-on content scripts on AI sites get **much heavier Web Store review**. Recommended:
> **publish v1.0.0 publicly** for the connector, and distribute **v1.1.0 privately** (Google Workspace
> "private" visibility or force-installed via enterprise policy) to the orgs that license governance.
> The ZIP `mdpdfm-connector-1.0.0.zip` is the connector-only build for public submission.


Everything below is paste-ready. The dashboard is at
https://chrome.google.com/webstore/devconsole (already signed in as mbubbtech@gmail.com).

## Steps

1. Click **+ New item** (top right).
2. Upload `C:\temp\md_to_pdf_tui\store\mdpdfm-connector-1.0.0.zip`.
3. Fill the **Store listing** tab with the fields below.
4. Fill the **Privacy** tab with the justifications below.
5. **Distribution** tab: Visibility = **Unlisted** (recommended — installable via link, no public browsing)
   or **Public** if you want it searchable.
6. Click **Submit for review**. Review typically takes 1–3 days for a listing this small.

## Store listing fields

**Item name**
```
Marksmith Connector
```

**Summary (132 chars max)**
```
Send ChatGPT, Gemini & Claude replies to the Marksmith desktop app as clean Markdown — one click, auto-detected, auto-converted.
```

**Description**
```
Marksmith Connector bridges your browser and the Marksmith desktop app (Windows).

WHAT IT DOES
• Click the toolbar button on chatgpt.com, gemini.google.com, or claude.ai to send the latest assistant reply to Marksmith
• Right-click → "Send full conversation" to capture every reply in the chat
• Select text on any site → right-click → "Send selection"

The desktop app detects which AI wrote the Markdown, cleans up assistant-specific
formatting quirks (citation pips, LaTeX delimiters, pseudo-headings), renders it with
your chosen theme, and can auto-convert it to a polished PDF — with math, syntax
highlighting, and a table of contents.

PRIVACY BY DESIGN
All data goes to 127.0.0.1 (your own machine) — nothing is sent to any external
server, no analytics, no tracking. The extension only acts when you invoke it.

REQUIRES
The Marksmith desktop app running on Windows with its local REST API enabled
(Automation → Local REST API). Default port 47821, configurable in Options.
```

**Category**: Tools (or Productivity → Workflow & Planning)
**Language**: English

**Store icon**: upload `C:\temp\md_to_pdf_tui\extension\icons\icon128.png`
**Screenshot**: upload `C:\temp\md_to_pdf_tui\store\screenshot-1280x800.png`

## Privacy tab

**Single purpose description**
```
Sends AI chat replies (converted to Markdown) from the active browser tab to the
user's local Marksmith desktop application at 127.0.0.1 for document conversion.
```

**Permission justifications**

| Permission | Justification (paste) |
| --- | --- |
| `activeTab` | Reads assistant messages from the current tab only when the user clicks the toolbar button or a context-menu item. |
| `scripting` | Injects the Markdown-extraction function into the active tab on user invocation; no persistent content scripts. |
| `contextMenus` | Provides "Send selection" and "Send full conversation" right-click actions. |
| `notifications` | Shows a confirmation or error notification after each send. |
| `storage` | Stores one user setting: the local API port number. |
| Host `http://127.0.0.1/*` | Delivers the extracted Markdown to the Marksmith desktop app's local REST API on the user's own machine. No remote hosts are contacted. |

**Data usage**: check **"This item does not collect or transmit user data"** — all
processing is local; content goes only to the user's own machine (127.0.0.1).

**Remote code**: No, this item does not use remote code.

## After approval

Installable link: `https://chromewebstore.google.com/detail/<item-id>` (shown on the
item page). Future version updates: bump `"version"` in manifest.json, re-zip, and
either re-upload in the dashboard or run `publish-update.ps1` (see that file for
one-time API setup).
