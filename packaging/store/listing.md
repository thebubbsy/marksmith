# Microsoft Store — listing copy

Paste these fields into Partner Center → your app → **Store listings → English (United States)**.
Marksmith can be submitted as an **EXE/MSI app** (Win32) — no MSIX required — using the
installer built by `packaging/installer/marksmith.iss`. (An MSIX path is also available under
`packaging/msix/` if you prefer.)

---

**Display name:** Marksmith

**Product name / title:** Marksmith — AI chats to polished documents

**Short title:** Marksmith

**Subtitle / short description (≤ 100 chars):**
Turn ChatGPT, Gemini & Claude replies into clean PDF and DOCX documents.

**Category:** Productivity
**Secondary category:** Developer tools

---

**Description:**

Marksmith turns Markdown — especially replies from ChatGPT, Gemini, and Claude — into
professional PDF and DOCX documents.

AI assistants all format the same way. It isn't bad formatting — it's recognizable
formatting: the same em-dashes, bold on everything, `\(LaTeX\)` delimiters, `【7†source】`
citation pips. Marksmith turns that into your formatting — re-theme it, shift the heading
structure, tone down the emphasis, swap the dashes — then renders it with proper math,
syntax highlighting, and Mermaid diagrams, ready to send.

WHAT YOU CAN DO
• Paste or drop Markdown and export a pixel-perfect PDF or a native DOCX
• Detect the AI source automatically and clean up each assistant's quirks
• Use it as a plain live Markdown editor — type on the left, watch it render on the right
• Personalize the output: themes, page width, heading shifts, emphasis, em-dash handling
• Render Mermaid diagrams and KaTeX math inline
• Automate it: watch a folder or the clipboard and convert hands-free
• Drive it from scripts via a local REST API, or from the companion browser extension

Marksmith is a native WinUI 3 app. The .NET runtime and Windows App SDK are bundled — there
is nothing else to install.

---

**Search terms / keywords (up to 7):**
markdown, pdf, docx, converter, chatgpt, markdown editor, mermaid

**What's new in this version:**
First public release of Marksmith 1.0.

---

**System requirements:**
- OS: Windows 11
- Architecture: x64
- The WebView2 runtime (ships with current Windows) is used for rendering

**Support contact:** mbubbtech@gmail.com
**Privacy policy URL:** https://thebubbsy.github.io/marksmith/privacy  ← publish `privacy-policy.md` (see packaging/README.md)
**Website:** https://github.com/thebubbsy/marksmith

---

## Assets to upload

| Slot | File |
| --- | --- |
| Store logo (300×300) | `packaging/store/assets/Store-Logo-300x300.png` |
| Hi-res icon (512×512 / 1080×1080) | `packaging/store/assets/Store-Logo-512x512.png`, `Store-Logo-1080x1080.png` |
| Screenshots (≥ 1366×768, up to 10) | `packaging/store/screenshots/*.png` |

## EXE/MSI submission — the details Partner Center asks for

- **Installer:** `Marksmith-Setup-x64.exe` (from the GitHub release / the Inno script)
- **Silent install parameters:** `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
- **Silent uninstall parameters:** `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
- **App detection / registry key** (Inno writes this per the AppId in `marksmith.iss`):
  `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{7E9B2C4A-3D5F-4A1E-9C8B-6F2D1A4B7E30}_is1`
  with value name `DisplayVersion` = `1.0.0`
