# marksmith-office — Office Capability plugin

100%-accurate Word renders for Marksmith: opens your generated `.docx` in the **real
installed Microsoft Word** (via NetOffice, MIT) and rasterizes the SmartArt / DrawingML shapes
to an image. Powers the **Word-exact** preview toggle (split view, Preview tab, and the
Looking Glass portal with blur/unblur).

## How it works

- `office-host.exe` (net8.0-windows) + NetOffice DLLs — a tiny console that drives Word on an
  STA thread: `detect`, `render <docx> <png>` (open → repaginate → InlineShape.EnhMetaFileBits
  → PNG, HTML round-trip fallback for floating shape groups), `verify <docx>` (shape counts).
- The app shells to it out-of-process (same pattern as the PlantUML JRE plugin) — no Office COM
  ever runs inside the Marksmith process.

## Install

The payload also ships next to the app (`plugins\marksmith-office\` in the install dir), so the
feature works out of the box. The artifact below is for machines that don't bundle it:

```
marksmith-office-host.zip   (the 12 host files, extracted to the plugin root)
```

`plugin.json` points at this repo's Releases (`marksmith-office-host.zip`, sha256 pinned).

## Publish steps (when this repo exists)

1. Attach `marksmith-office-host.zip` to a GitHub Release tagged `v1.0.0`
   (asset name must match the `url` above).
2. Keep `dist/plugin.json` + `dist/sha256.txt` in sync with the release asset hash.

## Build

```
dotnet build office-host/marksmith-office-host.csproj
# repack dist/marksmith-office-host.zip from office-host/bin/Debug/net8.0-windows10.0.19041.0/
# update dist/sha256.txt + the sha256 in dist/plugin.json
```

Requires Microsoft Office (any recent version; Word 2016+). Harmless when absent — the app
degrades to the HTML preview with a clear status message.
