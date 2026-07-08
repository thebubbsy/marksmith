# Marksmith roadmap

Where Marksmith is and where it's going. Grouped **Now / Next / Later** rather than by date — an indie
product ships when things are ready, and priority follows what users actually ask for.

> The honest north star: the single biggest lever isn't another desktop feature — it's **reach**
> (getting in front of the people who live in AI chats). That's why "Later" leads with a web/cross-
> platform path.

## Shipped ✅

- **AI source detection & cleanup** — fingerprints ChatGPT / Gemini / Claude and clears citation pips,
  LaTeX delimiters, pseudo-headings, "Copy code" lines; every fix counted.
- **Live editor & preview** — type Markdown, watch it render; sprite/blur only on paste or theme change.
- **PDF export** (Chromium print pipeline) and **native DOCX** (OpenXML, no Pandoc).
- **Editable Word equations (OMML)** — LaTeX becomes real, editable Cambria Math, not pictures.
- **DOCX that uses Word properly** — self-updating TOC, heading bookmarks + working links, repeating
  table headers, accessibility alt text, whole-document theme immersion.
- **Ten themes**, formatting personalization, Mermaid + KaTeX in preview/PDF.
- **Automation** — clipboard/watch-folder watchers, browser extension, local REST API.
- **Licensing** — Free / 14-day Trial / Pro, offline signed keys + Lemon Squeezy path.
- **EPUB & PPTX export** — EPUB 3 (chaptered, valid container) and themed PPTX decks (schema-valid).
- **Append to a running document** — each DOCX ingest added as a dated section to one growing `.docx`.

## Now — strengthen what's here

- [ ] **Waitlist + landing page live** on a real domain (validation gate).
- [x] **"Upgrade to Pro" nudge** — banner appears when the trial is low / expired.
- [x] **Advanced-formatting behind Pro** — gated; the section shows only for Pro/trial.
- [x] **Extension docs** — output-profile/auto-send options documented in `extension/README.md`.

## Next — the differentiators people will ask for

- [x] **PPTX & EPUB export** — both implemented and schema-validated.
- [x] **Append to a running document** — each ingest added as a dated section to one growing `.docx`.
- [x] **Mermaid diagrams in DOCX** — shipped *beyond* the plan: flowcharts are rebuilt as **native,
      editable Word shapes** (boxes/diamonds/connectors), not an embedded image. Unsupported diagram
      types fall back to the code block.
- [x] **Cleanup disclosed as a Word comment** — every DOCX exported from a normalized AI chat
      carries a real margin comment listing each applied fix. (Full word-level tracked-changes
      diffing remains a future refinement.)
- [x] **Branding kit** — cover page (logo, display title, date), document-wide custom typeface.
      (Letterhead/logo in the running header remains a refinement.)
- [x] **Batch convert a folder** — every .md becomes a themed PDF via the full pipeline (Pro).
- [x] **Export presets** — save the current look (theme + width + cleanup + formatting + diagram
      mode + branding) as a named preset, re-apply in one click.
- [x] **Offline-first** — mermaid, KaTeX (+fonts) and highlight.js are bundled and served locally;
      the app makes **zero CDN calls**, works without internet, and renders faster.
- [x] **Export diagram as image** — save any focused diagram as PNG or SVG.
- [x] **Batch to any format** — batch-convert a folder to PDF, DOCX, PPTX or EPUB.

## Later — raise the ceiling

- [ ] **Reach beyond Windows desktop** — a **web version** or the **browser extension doing the whole
      job**. This is the one change that grows the *market*, not just the product.
- [ ] **Teams / governance tier** — per-seat SaaS with the consent-based AI-usage dashboard + central
      config. This is where recurring revenue actually lives.
- [ ] **Delivery connectors** — send straight to SharePoint / Google Drive / Slack.
- [ ] **macOS** — if the web path doesn't cover it.

---

*Want something bumped up the list? The order follows demand — tell us what you'd pay for.*
