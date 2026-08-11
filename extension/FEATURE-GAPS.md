# Marksmith Connector — Extension Feature Gaps & The Standalone Pivot

**Current state (v2.10.0, MV3):** the connector is a thin remote control for the desktop app. It
captures AI-chat replies on ChatGPT / Gemini / Claude / Copilot, converts them to Markdown,
and posts them to the local Marksmith API (`127.0.0.1:47821`) — the desktop app does the real
work (rendering, DOCX/PDF export, themes, house style). It also has auto-send on conversation
end, a popup control center, context menus, notifications, and an Options page with one saved
output profile.

---

## Task 1 — 20+ features the extension is missing

### Capture & content quality
1. **Selection / single-reply capture** — capture only the selected text or one reply, instead
   of always grabbing the whole conversation.
2. **Image download & embedding** — fetch referenced images into the document (or embed them)
   so the exported DOCX/PDF actually contains them.
3. **Screenshot → document** — capture a screen region and embed it (or OCR/convert it).
4. **Clean math capture** — preserve rendered math as LaTeX (`$…$` / `$$…$$`) from the chat,
   not flattened glyph soup.
5. **Code-fence fidelity** — capture code blocks with their language tags intact, untouched by
   cleanup rules.
6. **Table capture** — convert rendered tables to real Markdown tables instead of flattened text.
7. **Multi-turn curation** — pick specific turns across the conversation and merge them into one
   document.
8. **In-extension citation-pip control** — toggle the `【7†source】` cleanup before anything is
   sent, not after it lands in the desktop app.

### Editing & control
9. **In-extension edit before convert** — a quick Markdown editor to fix the capture before it
   ships anywhere.
10. **Capture preview** — see exactly what was captured (Markdown/rendered) before sending.
11. **Per-capture output profile** — pick theme / page layout / formatting at capture time
    instead of one saved profile in Options.

### Conversion & delivery (the desktop-independence work — see Task 2)
12. **Standalone in-browser conversion** — the flagship: `Markdown → .docx` runs entirely inside
    the extension (WASM). No desktop app, no local API, no install.
13. **Per-capture format choice** — DOCX / PDF / PPTX / EPUB picker at capture time.
14. **In-browser preview before download** — render the finished document preview in the popup.
15. **Batch conversion** — convert several captures/threads in one go.
16. **Smart output naming** — derive the filename from the conversation title + date, dedupe.

### Delivery & integration
17. **One-click cloud delivery** — save directly to OneDrive / Google Drive / Dropbox, or attach
    to an email.
18. **Print directly** — send the result to a printer without opening Word.
19. **Template / house-style library** — browse and apply brand templates from the extension UI.
20. **`.dotx` import in-extension** — import a company template in the browser and apply its
    brand (fonts/colors/layout) — currently desktop-only.

### Automation & governance
21. **Capture-anywhere context menu** — right-click any selected text on *any* site →
    "Convert to Word" (the permission already exists; the capture surface doesn't).
22. **Auto-convert rules** — configurable triggers (site, idle time, keywords) beyond the single
    autosend toggle.
23. **Capture history** — local version history of captures; re-download or re-convert old ones.
24. **DLP / governance in-extension** — flag PII/credit cards/secrets before content leaves the
    browser (org mode).
25. **Fully offline operation** — convert with zero network beyond the AI chat itself.
26. **Sync across machines** — capture history and profiles synced via the browser account.
27. **Auto-open output** — open the produced document (or its folder) right after conversion.

---

## Task 2 — Architectural pivot: the whole app inside a free Chrome extension

### Why it relies on the desktop app today
The converter is **C# / .NET** (`MarkSmith.Core`): native OOXML generation, the SmartArt
constraint solver, KaTeX, SkiaSharp rasterization, plugins. A Chrome MV3 extension runs **JavaScript
only** — no native code, no .NET runtime, no file system. So today the extension is a remote
control and the desktop app is the engine.

### The pivot — WebAssembly
The repo already contains the seed: **`MarkSmith.Wasm`** — a Blazor WASM project that references
`MarkSmith.Core`. Ship the conversion pipeline as a **WASM module inside the extension**:

```
AI chat reply ──capture──▶ Markdown ──▶ MarkSmith.Core (compiled to WASM)
                                        │  OOXML builder · themes · math · diagrams
                                        ▼
                              .docx / .pdf bytes ──▶ downloads.download()  ──▶ user's file
```

Fully in-browser. Offline-capable. No desktop app, no local server, **free**.

### Why it's feasible (grounded in what exists)
- `MarkSmith.Core` is a plain .NET class library — WASM-compatible surface already used by
  `MarkSmith.Wasm`. DOCX output needs only `System.IO.Compression` (zip) + XML + SkiaSharp —
  all supported under WASM.
- The SmartArt constraint solver is pure C# — it compiles to WASM as-is.
- The HTML preview pipeline already emits browser-friendly HTML/SVG — it *is* the preview.

### Phased plan
1. **WASM spike** — publish `MarkSmith.Wasm` with one page: paste Markdown → download `.docx`.
   Proves `DocxExportService` (or the parts of it that matter) runs in-browser; resolve
   WASM-hostile pieces (SkiaSharp font loading, plugin services) by stubbing or trimming.
2. **Extension loader** — lazy-load the WASM asset in the service worker; wire capture → convert
   → `downloads.download()`. Kill the desktop dependency entirely for the core path.
3. **Parity** — themes, house-style profiles, math, diagrams, SmartArt (drop or WASM-port).
   Add the feature list from Task 1 on top (preview, batch, cloud delivery, history…).
4. **Distribution** — Chrome Web Store as a **free** listing; the desktop app stays the power
   tier (plugins, full studio, governance dashboards).

### Honest caveats
- WASM payload size (~2–5 MB) and cold-start cost — lazy-load + cache.
- PDF export under WASM needs a WASM-capable writer path (SkiaSharp-backed) — staged after DOCX.
- This is a multi-week project, not a one-shot change; the spike (phase 1) is the right first
  deliverable and is buildable now.
